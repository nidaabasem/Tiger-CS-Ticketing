using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CrmDocuments;
using TigerCS.Application.Modules.CrmDocuments.Dto;
using TigerCS.Application.Modules.CrmDocuments.Services;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Integrations.Modules.CrmIntegration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.CrmDocuments;

/// <summary>
/// The email provider cannot take a message back, so a database failure AFTER it accepted the document must never make the
/// request look unsent. Each case simulates SQL Server timing out (error 10060) at a chosen save and then retries the way Genesys
/// or the agent desk would — in a fresh request scope, with the same or a new Idempotency-Key — counting what the (fake) mail
/// provider actually received. No real email is sent and no CRM or SQL server is contacted.
///
/// Saves in one send are, in order: 1 = claim, 2 = write-ahead "Dispatching", 3+ = record "Sent" (retried) or "Failed".
/// </summary>
public class DocumentPostSendFailureTests
{
    private const string TestEmail = "nidaa.basim@tigergroup.ae";
    private static readonly byte[] Pdf = [.. "%PDF-1.7\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF\n"u8];

    private const string Listing =
        """{ "customerId": 9001, "leadId": 12345, "selectionRequired": false, "attachments": [ { "attachmentId": 5001, "name": "Sale and Purchase Agreement.pdf" } ] }""";

    private sealed class Harness
    {
        public DocumentServiceFixture Fixture { get; } = new();
        public FakeEmailSender Mail { get; } = new();
        public CrmDocumentCopyAppService Service { get; }

        public Harness()
        {
            Fixture.Options.PostSendSaveRetryDelayMilliseconds = 0;
            var handler = new StubHttpMessageHandler((r, _) => Task.FromResult(
                r.RequestUri!.AbsolutePath.EndsWith("GetCustomerDocuments", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Listing, Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Pdf) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } } }));
            var gateway = new CrmDocumentHttpGateway(
                new HttpClient(handler) { BaseAddress = new Uri("https://crm.example.test:8014/") },
                Options.Create(new CrmGatewayOptions { BaseUrl = "https://crm.example.test:8014/", SecretKey = "s" }), NullLogger<CrmDocumentHttpGateway>.Instance);
            var testing = new VerificationTestingOptions { Enabled = true, SmsOverride = "971564114806", EmailOverride = TestEmail, BlockRealCustomerDelivery = true };
            var sender = new EmailDocumentDeliveryChannelSender(Mail, CustomerNotificationPolicy.EnabledDefault, new VerificationTestingPolicy(testing, "UAT"));
            Service = new CrmDocumentCopyAppService(
                Fixture.Options, Fixture.Sessions, Fixture.Units, Fixture.Contacts, gateway,
                new CrmBuyerLookupAppService(Fixture.Buyers, NullLogger<CrmBuyerLookupAppService>.Instance),
                Fixture.Deliveries, [sender], Fixture.UnitOfWork, Fixture.Audit, Fixture.Clock, NullLogger<CrmDocumentCopyAppService>.Instance);
        }

        /// <summary>Each call is a new HTTP request: a fresh DbContext that sees only what the database holds.</summary>
        public Task<CrmDocumentCopyResult> Send(string key)
        {
            Fixture.Deliveries.BeginNewRequestScope();
            return Service.SendAsync(
                DocumentServiceFixture.Caller, new CrmDocumentCopyRequestDto(Fixture.SessionId, "Contract", null, null, null), key);
        }

        public CrmDocumentDeliveryRequest Row => Assert.Single(Fixture.Deliveries.All);
    }

    // ---- SQL times out after the provider accepted the email ----

    [Fact]
    public async Task SqlTimeoutAfterTheProviderAccepted_NeverThrows_AnswersUnconfirmed_AndLeavesTheSavedRowDispatching()
    {
        var h = new Harness();
        h.Fixture.UnitOfWork.FailSave = n => n >= 3;    // claim + write-ahead saved; every "Sent" save times out

        var result = await h.Send("k1");

        Assert.Equal(CrmDocumentCopyStatus.DeliveryUnconfirmed, result.Status);
        Assert.Equal(CrmDocumentCodes.DeliveryUnconfirmed, result.Code);
        Assert.False(result.Retryable);
        Assert.Single(h.Mail.Sent);
        Assert.Equal(DocumentDeliveryStatus.Dispatching, h.Fixture.Deliveries.PersistedStatus(h.Row));
        Assert.Equal(3, h.Fixture.UnitOfWork.SaveCalls - 2);            // the post-send save was attempted PostSendSaveAttempts (3) times
    }

    [Fact]
    public async Task RetryingTheSameKeyAfterThatTimeout_NeverEmailsAgain_EvenOnceTheStaleWindowHasPassed()
    {
        var h = new Harness();
        h.Fixture.UnitOfWork.FailSave = n => n >= 3 && n <= 5;           // only the first request's post-send saves time out

        await h.Send("k1");
        Assert.Single(h.Mail.Sent);

        var immediate = await h.Send("k1");
        Assert.Equal(CrmDocumentCopyStatus.Queued, immediate.Status);    // inside the stale window it may still be running: "ask again", nothing sent
        Assert.Single(h.Mail.Sent);

        h.Fixture.Clock.Advance(TimeSpan.FromMinutes(h.Fixture.Options.InProgressStaleAfterMinutes + 30));   // an InProgress claim would be retaken now
        var later = await h.Send("k1");

        Assert.Equal(CrmDocumentCopyStatus.DeliveryUnconfirmed, later.Status);
        Assert.True(later.Duplicate);
        Assert.Single(h.Mail.Sent);                                      // still exactly one email
        Assert.Equal(DocumentDeliveryStatus.Dispatching, h.Row.Status);
    }

    [Fact]
    public async Task ADifferentKeyForTheSameDocument_InsideTheDuplicateWindow_IsNotEmailedEither()
    {
        var h = new Harness();
        h.Fixture.UnitOfWork.FailSave = n => n >= 3 && n <= 5;

        await h.Send("k1");
        var again = await h.Send("k2");

        Assert.Equal(CrmDocumentCopyStatus.DeliveryUnconfirmed, again.Status);
        Assert.Single(h.Mail.Sent);
        Assert.Single(h.Fixture.Deliveries.All);                         // no second claim was even created
    }

    [Fact]
    public async Task AFreshRequestAfterTheDuplicateWindow_IsADeliberateNewSend()
    {
        var h = new Harness();
        h.Fixture.UnitOfWork.FailSave = n => n >= 3 && n <= 5;

        await h.Send("k1");
        h.Fixture.Clock.Advance(TimeSpan.FromMinutes(h.Fixture.Options.DuplicateSuppressionMinutes + 1));
        var fresh = await h.Send("k2");

        Assert.Equal(CrmDocumentCopyStatus.Sent, fresh.Status);
        Assert.Equal(2, h.Mail.Sent.Count);                              // the customer asked again after the window; the old key still sends nothing
        Assert.Equal(CrmDocumentCopyStatus.DeliveryUnconfirmed, (await h.Send("k1")).Status);
        Assert.Equal(2, h.Mail.Sent.Count);
    }

    [Fact]
    public async Task ATimeoutOnTheFirstSentSave_IsRetried_AndTheRequestEndsSent_WithOneEmail()
    {
        var h = new Harness();
        h.Fixture.UnitOfWork.FailSave = n => n == 3;                     // one timeout, then the database answers

        var result = await h.Send("k1");

        Assert.Equal(CrmDocumentCopyStatus.Sent, result.Status);
        Assert.False(result.Duplicate);
        Assert.Single(h.Mail.Sent);
        Assert.Equal(DocumentDeliveryStatus.Sent, h.Fixture.Deliveries.PersistedStatus(h.Row));
        Assert.Single(h.Fixture.Audit.Entries, e => e.Action == "CrmDocumentCopySent");   // retried saves write the audit entry once

        var replay = await h.Send("k1");
        Assert.True(replay.Duplicate);
        Assert.Single(h.Mail.Sent);
    }

    [Fact]
    public async Task AnUnconfirmedRequest_CannotBeRestartedByTheDomainEither()
    {
        var h = new Harness();
        h.Fixture.UnitOfWork.FailSave = n => n >= 3;
        await h.Send("k1");

        h.Fixture.Deliveries.BeginNewRequestScope();      // reload what the database holds: Dispatching
        var row = h.Row;
        Assert.Equal(DocumentDeliveryStatus.Dispatching, row.Status);
        Assert.False(row.IsStale(h.Fixture.Clock.GetUtcNow().UtcDateTime.AddDays(30), TimeSpan.FromMinutes(5)));
        Assert.Throws<InvalidOperationException>(() => row.Restart(h.Fixture.Clock.GetUtcNow().UtcDateTime));
    }

    // ---- SQL times out BEFORE the provider is called: nothing may be sent ----

    [Fact]
    public async Task IfTheWriteAheadSaveTimesOut_NothingIsEmailed_AndARetryAfterTheStaleWindowSendsExactlyOnce()
    {
        var h = new Harness();
        h.Fixture.UnitOfWork.FailSave = n => n == 2;                     // claim saved; "Dispatching" cannot be saved

        var first = await h.Send("k1");

        Assert.Equal(CrmDocumentCopyStatus.DeliveryFailed, first.Status);
        Assert.Equal(CrmDocumentCodes.DeliveryStateUnavailable, first.Code);
        Assert.True(first.Retryable);
        Assert.Empty(h.Mail.Sent);
        Assert.Equal(DocumentDeliveryStatus.InProgress, h.Fixture.Deliveries.PersistedStatus(h.Row));

        Assert.Equal(CrmDocumentCopyStatus.Queued, (await h.Send("k1")).Status);   // inside the stale window an identical request is just "queued"
        Assert.Empty(h.Mail.Sent);

        h.Fixture.Clock.Advance(TimeSpan.FromMinutes(h.Fixture.Options.InProgressStaleAfterMinutes + 1));
        var retried = await h.Send("k1");

        Assert.Equal(CrmDocumentCopyStatus.Sent, retried.Status);
        Assert.Single(h.Mail.Sent);
        Assert.Equal(DocumentDeliveryStatus.Sent, h.Fixture.Deliveries.PersistedStatus(h.Row));
    }

    [Fact]
    public async Task IfTheClaimSaveTimesOut_NothingIsEmailed()
    {
        var h = new Harness();
        h.Fixture.UnitOfWork.FailSave = n => n == 1;

        await Assert.ThrowsAsync<TimeoutException>(() => h.Send("k1"));   // before anything was fetched or sent

        Assert.Empty(h.Mail.Sent);
        Assert.Equal(CrmDocumentCopyStatus.Sent, (await h.Send("k1")).Status);
        Assert.Single(h.Mail.Sent);
    }

    // ---- the provider refuses or throws ----

    [Fact]
    public async Task AProviderRejection_WhoseFailedStatusCannotBeSaved_StillAnswersFailed_AndTheRetryIsNotSentBlind()
    {
        var h = new Harness();
        h.Mail.ThenTransient();
        h.Fixture.UnitOfWork.FailSave = n => n == 3;                     // the "Failed" save times out; the row stays Dispatching

        var first = await h.Send("k1");
        Assert.Equal(CrmDocumentCopyStatus.DeliveryFailed, first.Status);
        Assert.True(first.Retryable);

        Assert.Equal(CrmDocumentCopyStatus.Queued, (await h.Send("k1")).Status);
        h.Fixture.Clock.Advance(TimeSpan.FromMinutes(h.Fixture.Options.InProgressStaleAfterMinutes + 1));
        var retry = await h.Send("k1");
        Assert.Equal(CrmDocumentCopyStatus.DeliveryUnconfirmed, retry.Status);
        Assert.Single(h.Mail.Sent);                                      // conservative: unknown beats a possible double email
    }

    [Fact]
    public async Task AProviderRejectionThatIsSaved_StillAllowsTheDocumentedRetryWithTheSameKey()
    {
        var h = new Harness();
        h.Mail.ThenTransient();

        Assert.Equal(CrmDocumentCopyStatus.DeliveryFailed, (await h.Send("k1")).Status);
        Assert.Equal(DocumentDeliveryStatus.Failed, h.Fixture.Deliveries.PersistedStatus(h.Row));

        Assert.Equal(CrmDocumentCopyStatus.Sent, (await h.Send("k1")).Status);
        Assert.Equal(2, h.Mail.Sent.Count);                              // the refused attempt + the accepted one
        Assert.Equal(CrmDocumentCopyStatus.Sent, (await h.Send("k1")).Status);
        Assert.Equal(2, h.Mail.Sent.Count);                              // the replay sent nothing
    }

    [Fact]
    public async Task AnExceptionFromTheSender_IsUnconfirmed_BecauseTheProviderMayHaveAcceptedIt_AndIsNeverResent()
    {
        var h = new Harness();
        h.Mail.ThrowOnNextSend = new IOException("connection reset after DATA");

        var first = await h.Send("k1");
        Assert.Equal(CrmDocumentCopyStatus.DeliveryUnconfirmed, first.Status);

        h.Fixture.Clock.Advance(TimeSpan.FromHours(1));
        var retry = await h.Send("k1");

        Assert.Equal(CrmDocumentCopyStatus.DeliveryUnconfirmed, retry.Status);
        Assert.Single(h.Mail.Sent);
        Assert.Equal(DocumentDeliveryStatus.Dispatching, h.Fixture.Deliveries.PersistedStatus(h.Row));
    }

    // ---- what is preserved ----

    [Fact]
    public async Task TheSameSend_StillGoesOnlyToTheTestMailbox_WithTheCrmPdfUnchanged_WhenSqlTimesOutAfterIt()
    {
        var h = new Harness();
        h.Fixture.UnitOfWork.FailSave = n => n >= 3;

        await h.Send("k1");

        var mail = Assert.Single(h.Mail.Sent);
        Assert.Equal(TestEmail, mail.ToAddress);
        var attachment = Assert.Single(mail.Attachments!);
        Assert.Equal(Pdf, attachment.Content);
        Assert.Equal("application/pdf", attachment.ContentType);
    }

    [Fact]
    public async Task AWronglyOwnedRecord_IsStillRefused_BeforeAnyWriteAheadOrEmail()
    {
        var h = new Harness();
        h.Fixture.UnitOfWork.FailSave = n => false;

        h.Fixture.Deliveries.BeginNewRequestScope();
        var result = await h.Service.SendAsync(
            DocumentServiceFixture.Caller, new CrmDocumentCopyRequestDto(h.Fixture.SessionId, "Contract", null, "9999", null), "k-other");

        Assert.Equal(CrmDocumentCopyStatus.OwnershipMismatch, result.Status);
        Assert.Empty(h.Mail.Sent);
        Assert.Empty(h.Fixture.Deliveries.All);
    }
}

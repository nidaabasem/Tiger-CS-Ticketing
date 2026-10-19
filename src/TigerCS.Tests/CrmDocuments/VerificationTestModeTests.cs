using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.CrmDocuments;
using TigerCS.Application.Modules.CrmDocuments.Dto;
using TigerCS.Application.Modules.CrmDocuments.Services;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.CrmDocuments;

/// <summary>
/// Verification test mode (<c>VerificationTesting</c>): real CRM customers are looked up and verified as usual, but the OTP SMS/email
/// and the document email are redirected to the test inboxes. Everything runs over fakes — <b>nothing is sent to anyone</b>.
/// </summary>
public class VerificationTestModeTests
{
    private const string TestSms = "971564114806";
    private const string TestEmail = "nidaa.basim@tigergroup.ae";
    private const string CustomerEmail = "ahmed.ali@example.com";
    private const string CustomerMobileDigits = "971501234567";   // OtpServiceFixture.Phone as CRM holds it
    private static readonly Guid Caller = OtpServiceFixture.Caller;

    private static VerificationTestingOptions Valid() =>
        new() { Enabled = true, SmsOverride = TestSms, EmailOverride = TestEmail, BlockRealCustomerDelivery = true };

    private static OtpServiceFixture OtpOn(VerificationTestingOptions? options = null, string environment = "UAT")
    {
        var f = new OtpServiceFixture();
        f.EnableSms();
        f.UseTestMode(options ?? Valid(), environment);
        return f;
    }

    // ------------------------------------------------------------- policy

    [Fact]
    public void Policy_Off_PassesTheCustomersOwnDetailsThroughUnchanged_InEveryEnvironment()
    {
        foreach (var env in new[] { "Production", "UAT", "Development" })
        {
            var policy = new VerificationTestingPolicy(new VerificationTestingOptions(), env);
            Assert.False(policy.Enabled);
            Assert.False(policy.IsBlocked);
            Assert.Equal(new TestDeliveryRoute(true, CustomerEmail, false), policy.RouteEmail(CustomerEmail));
            Assert.Equal(new TestDeliveryRoute(true, CustomerMobileDigits, false), policy.RouteSms(CustomerMobileDigits));
        }
    }

    [Theory]
    [InlineData("Local")]
    [InlineData("Development")]
    [InlineData("UAT")]
    [InlineData("uat")]
    [InlineData("Testing")]
    public void Policy_On_ForAllowedEnvironments_RoutesEverythingToTheOverrides(string environment)
    {
        var policy = new VerificationTestingPolicy(Valid(), environment);

        Assert.False(policy.IsBlocked);
        Assert.Equal(new TestDeliveryRoute(true, TestEmail, true), policy.RouteEmail(CustomerEmail));
        Assert.Equal(new TestDeliveryRoute(true, TestSms, true), policy.RouteSms(CustomerMobileDigits));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("")]
    public void Policy_On_InANonAllowedEnvironment_IsBlocked_AndRoutesNothing(string environment)
    {
        var policy = new VerificationTestingPolicy(Valid(), environment);

        Assert.True(policy.IsBlocked);
        Assert.NotNull(policy.ConfigurationError);
        Assert.False(policy.RouteEmail(CustomerEmail).Allowed);
        Assert.False(policy.RouteSms(CustomerMobileDigits).Allowed);
        Assert.Empty(policy.RouteEmail(CustomerEmail).Destination);
    }

    [Theory]
    [InlineData(null, TestEmail, true)]
    [InlineData("", TestEmail, true)]
    [InlineData("+971564114806", TestEmail, true)]    // '+' is not accepted: digits only, as the SMS pipeline expects
    [InlineData("12345", TestEmail, true)]
    [InlineData(TestSms, null, true)]
    [InlineData(TestSms, "not-an-email", true)]
    [InlineData(TestSms, TestEmail, false)]            // BlockRealCustomerDelivery off
    public void Policy_On_WithIncompleteOrInvalidConfiguration_FailsClosed(string? sms, string? email, bool blockReal)
    {
        var policy = new VerificationTestingPolicy(
            new VerificationTestingOptions { Enabled = true, SmsOverride = sms, EmailOverride = email, BlockRealCustomerDelivery = blockReal }, "UAT");

        Assert.True(policy.IsBlocked);
        Assert.False(policy.RouteEmail(CustomerEmail).Allowed);
        Assert.False(policy.RouteSms(CustomerMobileDigits).Allowed);
    }

    // ------------------------------------------------------- OTP: SMS / email

    [Fact]
    public async Task SmsOtp_GoesToTheOverrideNumber_NeverTheCustomer_AndTheChallengeKeepsTheCustomersIdentity()
    {
        var f = OtpOn();

        var result = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101", "Sms");

        Assert.Equal(CustomerOtpStatus.CodeSent, result.Status);
        var sms = Assert.Single(f.Sms.Sent);
        Assert.Equal(TestSms, sms.Destination);
        Assert.Empty(f.Email.Sent);
        // The challenge is still bound to — and masks — the customer's real mobile, not the test number.
        Assert.Equal(CustomerOtpAppService.MaskMobile(CustomerMobileDigits), f.Store.Single().MaskedDestination);
        Assert.Equal(CustomerOtpAppService.MaskMobile(CustomerMobileDigits), result.MaskedDestination);
        Assert.Equal(9001, f.Store.Single().CrmCustomerId);
        Assert.Equal(12345, f.Store.Single().CrmLeadId);
    }

    [Fact]
    public async Task EmailOtp_GoesToTheOverrideMailbox_NeverTheCustomer()
    {
        var f = OtpOn();

        var result = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");

        Assert.Equal(CustomerOtpStatus.CodeSent, result.Status);
        var mail = Assert.Single(f.Email.Sent);
        Assert.Equal(TestEmail, mail.ToAddress);
        Assert.Empty(f.Sms.Sent);
        Assert.Equal("a***@e***.com", f.Store.Single().MaskedDestination);
        Assert.DoesNotContain(f.Email.Sent, m => m.ToAddress == CustomerEmail);
    }

    [Theory]
    [InlineData("Sms")]
    [InlineData("Email")]
    public async Task Resend_GoesToTheOverrideToo_AndNothingReachesTheCustomer(string channel)
    {
        var f = OtpOn();
        var first = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101", channel);
        f.Clock.Advance(TimeSpan.FromSeconds(61));

        var resent = await f.Service.ResendAsync(Caller, first.ChallengeId);

        Assert.Equal(CustomerOtpStatus.CodeSent, resent.Status);
        if (channel == "Sms")
        {
            Assert.Equal(2, f.Sms.Sent.Count);
            Assert.All(f.Sms.Sent, m => Assert.Equal(TestSms, m.Destination));
        }
        else
        {
            Assert.Equal(2, f.Email.Sent.Count);
            Assert.All(f.Email.Sent, m => Assert.Equal(TestEmail, m.ToAddress));
        }
    }

    [Fact]
    public async Task ARetriedSend_ForALiveChallenge_IsAlsoRedirected()
    {
        var f = OtpOn();
        f.Options.OtpMinResendSeconds = 0;
        await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");

        await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");   // pending challenge → resend path

        Assert.Equal(2, f.Email.Sent.Count);
        Assert.All(f.Email.Sent, m => Assert.Equal(TestEmail, m.ToAddress));
    }

    [Fact]
    public async Task TheCodeSentToTheTestInbox_VerifiesNormally_AndProducesTheCustomersOtpSession()
    {
        var f = OtpOn();
        var sent = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");

        var wrong = await f.Service.VerifyAsync(Caller, sent.ChallengeId, "000000");
        var verified = await f.Service.VerifyAsync(Caller, sent.ChallengeId, f.LastEmailedCode());

        Assert.Equal(CustomerOtpStatus.InvalidCode, wrong.Status);
        Assert.Equal(CustomerOtpStatus.Verified, verified.Status);
        Assert.NotNull(verified.Session);
        // The session is for the customer's unit and contact — the test inbox appears nowhere in it.
        Assert.DoesNotContain(TestEmail, System.Text.Json.JsonSerializer.Serialize(verified));
        Assert.DoesNotContain(TestSms, System.Text.Json.JsonSerializer.Serialize(verified));
    }

    [Fact]
    public async Task NoRealCustomerReceivesAnOtp_AcrossSendResendAndBothChannels()
    {
        var f = OtpOn();
        var sms = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101", "Sms");
        var email = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101", "Email");
        f.Clock.Advance(TimeSpan.FromSeconds(61));
        await f.Service.ResendAsync(Caller, sms.ChallengeId);
        await f.Service.ResendAsync(Caller, email.ChallengeId);

        Assert.NotEmpty(f.Sms.Sent);
        Assert.NotEmpty(f.Email.Sent);
        Assert.All(f.Sms.Sent, m => Assert.NotEqual(CustomerMobileDigits, m.Destination));
        Assert.All(f.Sms.Sent, m => Assert.Equal(TestSms, m.Destination));
        Assert.All(f.Email.Sent, m => Assert.Equal(TestEmail, m.ToAddress));
    }

    [Fact]
    public async Task TheOtpCode_IsNeverLogged()
    {
        var f = OtpOn();
        var log = new CapturingLogger();
        f.Logger = log;
        f.UseTestMode(Valid());
        var sent = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101", "Sms");
        f.Clock.Advance(TimeSpan.FromSeconds(61));
        await f.Service.ResendAsync(Caller, sent.ChallengeId);

        Assert.NotEmpty(log.Lines);
        foreach (var code in f.Codes.Issued)
        {
            Assert.DoesNotContain(log.Lines, l => l.Contains(code, StringComparison.Ordinal));
        }
    }

    // ------------------------------------------- OTP: invalid configuration

    [Theory]
    [InlineData("UAT", null, TestEmail)]
    [InlineData("UAT", TestSms, "bad")]
    [InlineData("Production", TestSms, TestEmail)]
    public async Task OtpSendAndResend_AreBlocked_WhenTestModeIsEnabledButInvalid_AndNothingIsSentOrStored(string environment, string? sms, string? email)
    {
        // Seed a live challenge first, with test mode off, so resend has something real to act on.
        var f = new OtpServiceFixture();
        f.EnableSms();
        var live = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");
        f.Email.Sent.Clear();
        f.Clock.Advance(TimeSpan.FromSeconds(61));
        var storedBefore = f.Store.Committed.Count;

        f.UseTestMode(new VerificationTestingOptions { Enabled = true, SmsOverride = sms, EmailOverride = email, BlockRealCustomerDelivery = true }, environment);

        var send = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1102", "Sms");
        var sendEmail = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1102");
        var resend = await f.Service.ResendAsync(Caller, live.ChallengeId);

        Assert.All([send, sendEmail, resend], r =>
        {
            Assert.Equal(CustomerOtpStatus.TestModeMisconfigured, r.Status);
            Assert.Equal(CustomerOtpCodes.TestModeMisconfigured, r.Code);
        });
        Assert.Empty(f.Email.Sent);
        Assert.Empty(f.Sms.Sent);
        Assert.Equal(storedBefore, f.Store.Committed.Count);
    }

    [Fact]
    public async Task Verification_StillWorks_WhenTestModeIsInvalid_ItOnlyBlocksDelivery()
    {
        var f = new OtpServiceFixture();
        var sent = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");
        var code = f.LastEmailedCode();
        f.UseTestMode(new VerificationTestingOptions { Enabled = true }, "UAT");

        var verified = await f.Service.VerifyAsync(Caller, sent.ChallengeId, code);

        Assert.Equal(CustomerOtpStatus.Verified, verified.Status);
    }

    // ------------------------------------------------- Production unchanged

    [Fact]
    public async Task Production_WithTestModeOff_SendsToTheCustomersOwnAddressAndNumber_Unchanged()
    {
        var f = new OtpServiceFixture();   // policy: default options, "Production"
        f.EnableSms();

        var email = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");
        var sms = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101", "Sms");

        Assert.Equal(CustomerEmail, Assert.Single(f.Email.Sent).ToAddress);
        Assert.Equal(CustomerMobileDigits, Assert.Single(f.Sms.Sent).Destination);
        Assert.Equal(CustomerOtpStatus.CodeSent, email.Status);
        Assert.Equal(CustomerOtpStatus.CodeSent, sms.Status);
    }

    [Fact]
    public async Task Production_WithTestModeEnabled_IsRefused_AndNeverRedirectsOrSends()
    {
        var f = new OtpServiceFixture();
        f.UseTestMode(Valid(), "Production");

        var result = await f.Service.SendAsync(Caller, OtpServiceFixture.Phone, "1101");

        Assert.Equal(CustomerOtpStatus.TestModeMisconfigured, result.Status);
        Assert.Empty(f.Email.Sent);
    }

    // ----------------------------------------------------------- documents

    private sealed class DocumentHarness
    {
        public DocumentServiceFixture Fixture { get; } = new();
        public FakeEmailSender Mail { get; } = new();
        public CrmDocumentCopyAppService Service { get; }

        public DocumentHarness(VerificationTestingOptions options, string environment)
        {
            var sender = new EmailDocumentDeliveryChannelSender(Mail, CustomerNotificationPolicy.EnabledDefault, new VerificationTestingPolicy(options, environment));
            Service = new CrmDocumentCopyAppService(
                Fixture.Options, Fixture.Sessions, Fixture.Units, Fixture.Contacts, Fixture.Gateway,
                new CrmBuyerLookupAppService(Fixture.Buyers, NullLogger<CrmBuyerLookupAppService>.Instance),
                Fixture.Deliveries, [sender], Fixture.UnitOfWork, Fixture.Audit, Fixture.Clock, NullLogger<CrmDocumentCopyAppService>.Instance);
            Fixture.Own("REC-1");
        }

        public Task<CrmDocumentCopyResult> SendGenesys() =>
            Service.SendAsync(DocumentServiceFixture.Caller, new CrmDocumentCopyRequestDto(Fixture.SessionId, "Contract", null, "REC-1", null), "doc-key-1");

        public Task<CrmDocumentCopyResult> SendAgentDesk() =>
            Service.SendToCustomerAsync(
                DocumentServiceFixture.Caller,
                new AgentDocumentRecipient(DocumentServiceFixture.CustomerId, DocumentServiceFixture.LeadId, "1204", "customer@example.com", "Ahmed Al-Farsi"),
                CrmDocumentType.Contract, "REC-1", "agent-key-1");
    }

    [Fact]
    public async Task GenesysDocument_IsEmailedToTheTestMailbox_WithTheOriginalDestinationKeptSeparate()
    {
        var h = new DocumentHarness(Valid(), "UAT");

        var result = await h.SendGenesys();

        Assert.Equal(CrmDocumentCopyStatus.Sent, result.Status);
        var mail = Assert.Single(h.Mail.Sent);
        Assert.Equal(TestEmail, mail.ToAddress);
        Assert.NotEmpty(Assert.Single(mail.Attachments!).Content);
        Assert.StartsWith("[UAT TEST]", mail.Subject);
        Assert.DoesNotContain("customer@example.com", mail.Body);                 // only the masked original is mentioned
        Assert.Contains("c***@e***.com", mail.Body);
        // The recorded / reported destination is still the customer's (masked) CRM address.
        Assert.Equal("c***@e***.com", result.MaskedDestination);
        Assert.Contains(h.Fixture.Audit.Entries, e => e.AfterValue!.Contains("Destination=c***@e***.com") && e.AfterValue.Contains("TestRedirect=true"));
        Assert.DoesNotContain(h.Mail.Sent, m => m.ToAddress == "customer@example.com");
    }

    [Fact]
    public async Task AgentDeskDocument_IsEmailedToTheTestMailbox_Too()
    {
        var h = new DocumentHarness(Valid(), "UAT");

        var result = await h.SendAgentDesk();

        Assert.Equal(CrmDocumentCopyStatus.Sent, result.Status);
        Assert.Equal(TestEmail, Assert.Single(h.Mail.Sent).ToAddress);
        Assert.DoesNotContain(h.Mail.Sent, m => m.ToAddress == "customer@example.com");
    }

    [Fact]
    public async Task Document_OwnershipAndVerificationChecks_StillApplyInTestMode()
    {
        var h = new DocumentHarness(Valid(), "UAT");

        // A record the verified customer does not own is still refused — the redirect does not relax ownership.
        var foreign = await h.Service.SendAsync(
            DocumentServiceFixture.Caller, new CrmDocumentCopyRequestDto(h.Fixture.SessionId, "Contract", null, "REC-OTHER", null), "doc-key-2");
        // An unknown verification session is still refused.
        var unverified = await h.Service.SendAsync(
            DocumentServiceFixture.Caller, new CrmDocumentCopyRequestDto(Guid.NewGuid(), "Contract", null, "REC-1", null), "doc-key-3");

        Assert.NotEqual(CrmDocumentCopyStatus.Sent, foreign.Status);
        Assert.NotEqual(CrmDocumentCopyStatus.Sent, unverified.Status);
        Assert.Empty(h.Mail.Sent);
    }

    [Theory]
    [InlineData("UAT", null, TestEmail)]
    [InlineData("UAT", TestSms, "bad")]
    [InlineData("Production", TestSms, TestEmail)]
    public async Task Document_IsBlocked_WhenTestModeIsEnabledButInvalid_ForBothFlows_AndNothingIsClaimedOrSent(string environment, string? sms, string? email)
    {
        var h = new DocumentHarness(new VerificationTestingOptions { Enabled = true, SmsOverride = sms, EmailOverride = email, BlockRealCustomerDelivery = true }, environment);

        var genesys = await h.SendGenesys();
        var agent = await h.SendAgentDesk();

        Assert.All([genesys, agent], r =>
        {
            Assert.Equal(CrmDocumentCopyStatus.DeliveryFailed, r.Status);
            Assert.Equal(CrmDocumentCodes.TestModeMisconfigured, r.Code);
        });
        Assert.Empty(h.Mail.Sent);
        Assert.Empty(h.Fixture.Deliveries.All);
        Assert.Empty(h.Fixture.Gateway.DownloadCalls);
    }

    [Fact]
    public async Task Document_InProduction_WithTestModeOff_GoesToTheCustomerUnchanged()
    {
        var h = new DocumentHarness(new VerificationTestingOptions(), "Production");

        var genesys = await h.SendGenesys();

        Assert.Equal(CrmDocumentCopyStatus.Sent, genesys.Status);
        var mail = Assert.Single(h.Mail.Sent);
        Assert.Equal("customer@example.com", mail.ToAddress);
        Assert.DoesNotContain("[UAT TEST]", mail.Subject);
        Assert.DoesNotContain("TEST MODE", mail.Body);
    }

    private sealed class CapturingLogger : ILogger<CustomerOtpAppService>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + exception);
    }
}

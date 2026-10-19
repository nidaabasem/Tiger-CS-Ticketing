using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CrmDocuments;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
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
/// Attachment documents (ReservationForm, TigerContract, RegistrationReceipt) are downloaded through CRM's authenticated
/// <c>POST /TicketingSystem/GetCustomerDocumentFile</c> — never from the private storage <c>fileUrl</c>. Everything runs over stubs;
/// nothing is sent and no storage is contacted.
/// </summary>
public class CrmDocumentFileEndpointTests
{
    private const string BaseUrl = "https://crm.example.test:8014/";
    private const string Secret = "test-only-secret";
    private static readonly byte[] Pdf = [.. "%PDF-1.7\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF\n"u8];

    private static CrmDocumentHttpGateway Gateway(StubHttpMessageHandler handler, string? secret = Secret, Action<CrmGatewayOptions>? configure = null)
    {
        var opts = new CrmGatewayOptions { BaseUrl = BaseUrl, SecretKey = secret };
        configure?.Invoke(opts);
        return new CrmDocumentHttpGateway(
            new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) }, Options.Create(opts), NullLogger<CrmDocumentHttpGateway>.Instance);
    }

    private static HttpResponseMessage FileResponse(byte[] bytes, string contentType = "application/pdf", HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue(contentType) } } };

    private static StubHttpMessageHandler Serve(byte[] bytes, string contentType = "application/pdf", HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(FileResponse(bytes, contentType, status)));

    private static Task<CrmDocumentContent?> Download(
        StubHttpMessageHandler handler, CrmDocumentType type = CrmDocumentType.Contract, string recordId = "5001", string label = "Sale and Purchase Agreement.pdf",
        Action<CrmGatewayOptions>? configure = null) =>
        Gateway(handler, configure: configure).DownloadAsync(type, 9001, 12345, new CrmDocumentRecord(recordId, label, "https://private-bucket.s3.amazonaws.com/x.pdf?X-Amz-Signature=SECRET"));

    // ------------------------------------------------------------- success

    [Theory]
    [InlineData(CrmDocumentType.Contract, "TigerContract")]
    [InlineData(CrmDocumentType.ReservationForm, "ReservationForm")]
    [InlineData(CrmDocumentType.RegistrationReceipt, "RegistrationReceipt")]
    public async Task Download_PostsTheOwnershipKey_ToTheAuthenticatedEndpoint_AndReturnsTheValidatedPdf(CrmDocumentType type, string crmName)
    {
        string? body = null;
        var handler = new StubHttpMessageHandler(async (r, _) =>
        {
            body = await r.Content!.ReadAsStringAsync();
            return FileResponse(Pdf);
        });

        var content = await Download(handler, type);

        var request = handler.LastRequest!;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://crm.example.test:8014/TicketingSystem/GetCustomerDocumentFile", request.RequestUri!.ToString());
        Assert.Equal(Secret, request.Headers.GetValues("X-SECRET-KEY").Single());
        using var json = JsonDocument.Parse(body!);
        Assert.Equal(9001, json.RootElement.GetProperty("CustomerID").GetInt32());
        Assert.Equal(12345, json.RootElement.GetProperty("LeadID").GetInt32());
        Assert.Equal(crmName, json.RootElement.GetProperty("DocumentType").GetString());
        Assert.Equal(5001, json.RootElement.GetProperty("AttachmentId").GetInt64());
        Assert.Equal(4, json.RootElement.EnumerateObject().Count());
        Assert.Equal(1, handler.CallCount);                       // the private storage URL was never requested

        Assert.Equal("5001", content!.RecordId);
        Assert.Equal(Pdf, content.Bytes);
        Assert.Equal("application/pdf", content.ContentType);
        Assert.Equal("Sale and Purchase Agreement.pdf", content.FileName);
    }

    [Fact]
    public async Task TheStorageFileUrl_IsNeverRequested_AndItsHostNeedNotBeAllowListed()
    {
        var handler = Serve(Pdf);

        await Download(handler);

        Assert.DoesNotContain("amazonaws", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task AFilenameFromCrm_IsSanitised_AndGetsThePdfExtension()
    {
        var content = await Download(Serve(Pdf), label: @"..\..\evil;name"" Contract.docx");

        Assert.EndsWith(".pdf", content!.FileName);
        Assert.DoesNotContain("\\", content.FileName);
        Assert.DoesNotContain("/", content.FileName);
        Assert.DoesNotContain("\"", content.FileName);
        Assert.DoesNotContain(";", content.FileName);
    }

    [Fact]
    public async Task AMatchingChecksumFromCrm_IsAccepted()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var r = FileResponse(Pdf);
            r.Headers.Add("X-Content-SHA256", Convert.ToHexString(SHA256.HashData(Pdf)));
            return Task.FromResult(r);
        });

        Assert.Equal(Pdf, (await Download(handler))!.Bytes);
    }

    [Fact]
    public async Task ListingWithoutAFileUrl_StillYieldsTheAttachmentRecords()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{ "customerId": 9001, "leadId": 12345, "attachments": [ { "attachmentId": 5001, "name": "A.pdf" } ] }""", Encoding.UTF8, "application/json")
        }));

        var listing = await Gateway(handler).ListAsync(CrmDocumentType.Contract, 9001, 12345);

        Assert.Equal("5001", Assert.Single(listing.Records).RecordId);
    }

    // ------------------------------------------------- missing / not theirs

    [Fact]
    public async Task A404_IsADocumentThatDoesNotExistForThisCustomerLeadAndType_NothingIsReturned()
    {
        // CRM answers 404 both for "no such attachment" and for "attachment belongs to someone else / another lead / another type".
        Assert.Null(await Download(Serve([], status: HttpStatusCode.NotFound)));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("5001; DROP TABLE x")]
    [InlineData("../5001")]
    [InlineData("-1")]
    [InlineData("")]
    public async Task ANonNumericAttachmentId_IsRefusedLocally_AndCrmIsNeverAsked(string recordId)
    {
        var handler = Serve(Pdf);

        var ex = await Assert.ThrowsAsync<CrmDocumentSourceException>(() => Download(handler, recordId: recordId));

        Assert.Equal(CrmDocumentSourceFailure.InvalidResponse, ex.Failure);
        Assert.Equal(0, handler.CallCount);
    }

    // --------------------------------------------------------- CRM failures

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, CrmDocumentSourceFailure.RequestRejected)]
    [InlineData(HttpStatusCode.Unauthorized, CrmDocumentSourceFailure.AuthenticationFailed)]
    [InlineData(HttpStatusCode.Forbidden, CrmDocumentSourceFailure.AccessDenied)]
    [InlineData(HttpStatusCode.InternalServerError, CrmDocumentSourceFailure.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CrmDocumentSourceFailure.Unavailable)]
    [InlineData(HttpStatusCode.Found, CrmDocumentSourceFailure.ReferenceRejected)]
    public async Task CrmFailures_AreClassified_AndNothingIsReturned(HttpStatusCode status, CrmDocumentSourceFailure expected)
    {
        var ex = await Assert.ThrowsAsync<CrmDocumentSourceException>(() => Download(Serve([], status: status)));

        Assert.Equal(expected, ex.Failure);
    }

    [Fact]
    public async Task AnUnreachableCrm_IsUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("down"));

        var ex = await Assert.ThrowsAsync<CrmDocumentSourceException>(() => Download(handler));

        Assert.Equal(CrmDocumentSourceFailure.Unavailable, ex.Failure);
    }

    [Fact]
    public async Task WithoutTheServerSecret_NothingIsCalled()
    {
        var handler = Serve(Pdf);

        var ex = await Assert.ThrowsAsync<CrmDocumentSourceException>(() => Gateway(handler, secret: " ")
            .DownloadAsync(CrmDocumentType.Contract, 9001, 12345, new CrmDocumentRecord("5001", "x", "")));

        Assert.Equal(CrmDocumentSourceFailure.AuthenticationFailed, ex.Failure);
        Assert.Equal(0, handler.CallCount);
    }

    // --------------------------------------------------- file is validated

    public static TheoryData<string, byte[], string> BadFiles => new()
    {
        { "an HTML login page", Encoding.UTF8.GetBytes("<html>Login</html>"), "text/html" },
        { "a PNG image", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], "application/pdf" },
        { "plain text", Encoding.UTF8.GetBytes("MOCK FIXTURE — not a real document."), "application/pdf" },
        { "a truncated PDF (no %%EOF)", [.. "%PDF-1.7\n1 0 obj\n<<>>\nendobj\nstream ......."u8], "application/pdf" },
        { "an empty body", [], "application/pdf" },
    };

    [Theory]
    [MemberData(nameof(BadFiles))]
    public async Task ABodyThatIsNotACompletePdf_IsRefused_AndNeverReturned(string _, byte[] bytes, string contentType)
    {
        var ex = await Assert.ThrowsAsync<CrmDocumentSourceException>(() => Download(Serve(bytes, contentType)));

        Assert.Equal(CrmDocumentSourceFailure.InvalidResponse, ex.Failure);
    }

    [Fact]
    public async Task AChecksumMismatch_IsRefused()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var r = FileResponse(Pdf);
            r.Headers.Add("X-Content-SHA256", new string('0', 64));
            return Task.FromResult(r);
        });

        var ex = await Assert.ThrowsAsync<CrmDocumentSourceException>(() => Download(handler));

        Assert.Equal(CrmDocumentSourceFailure.InvalidResponse, ex.Failure);
    }

    [Fact]
    public async Task AnOversizedBody_IsReadOnlyUpToTheCap_SoTheServiceCanRefuseIt()
    {
        var big = new byte[1000];
        Pdf.CopyTo(big, 0);

        var content = await Download(Serve(big), configure: o => o.MaxDocumentBytes = 100);

        Assert.Equal(101, content!.Bytes.Length);   // the cap + 1: enough for the service to see "too large", never the whole body
    }
}

/// <summary>
/// Genesys flow end to end through the REAL gateway, document service and email sender over stubs: the PDF CRM returns is emailed
/// as an attachment, to the test mailbox only while <c>VerificationTesting</c> is on, and "Sent" is recorded only once the email
/// provider accepts it.
/// </summary>
public class SendDocumentEndToEndTests
{
    private const string TestEmail = "nidaa.basim@tigergroup.ae";
    private static readonly byte[] Pdf = [.. "%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF\n"u8];

    private sealed class Harness
    {
        public DocumentServiceFixture Fixture { get; } = new();
        public FakeEmailSender Mail { get; } = new();
        public List<string> CrmCalls { get; } = [];
        public CrmDocumentCopyAppService Service { get; }

        public Harness(VerificationTestingOptions testing, string environment, string listing, Func<HttpResponseMessage>? file = null)
        {
            var handler = new StubHttpMessageHandler((r, _) =>
            {
                CrmCalls.Add(r.RequestUri!.AbsolutePath);
                return Task.FromResult(r.RequestUri.AbsolutePath.EndsWith("GetCustomerDocuments", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listing, Encoding.UTF8, "application/json") }
                    : file?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Pdf) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } } });
            });
            var gateway = new CrmDocumentHttpGateway(
                new HttpClient(handler) { BaseAddress = new Uri("https://crm.example.test:8014/") },
                Options.Create(new CrmGatewayOptions { BaseUrl = "https://crm.example.test:8014/", SecretKey = "s" }), NullLogger<CrmDocumentHttpGateway>.Instance);
            var sender = new EmailDocumentDeliveryChannelSender(Mail, CustomerNotificationPolicy.EnabledDefault, new VerificationTestingPolicy(testing, environment));
            Service = new CrmDocumentCopyAppService(
                Fixture.Options, Fixture.Sessions, Fixture.Units, Fixture.Contacts, gateway,
                new CrmBuyerLookupAppService(Fixture.Buyers, NullLogger<CrmBuyerLookupAppService>.Instance),
                Fixture.Deliveries, [sender], Fixture.UnitOfWork, Fixture.Audit, Fixture.Clock, NullLogger<CrmDocumentCopyAppService>.Instance);
        }

        public Task<CrmDocumentCopyResult> Send(string type = "Contract", string? recordId = null, string key = "k1") =>
            Service.SendAsync(DocumentServiceFixture.Caller, new CrmDocumentCopyRequestDto(Fixture.SessionId, type, null, recordId, null), key);
    }

    private static string One(string name = "Sale and Purchase Agreement.pdf") =>
        $$"""{ "customerId": 9001, "leadId": 12345, "selectionRequired": false, "attachments": [ { "attachmentId": 5001, "fileUrl": "https://private.s3.example/x.pdf?sig=1", "name": "{{name}}" } ] }""";

    private const string Two =
        """{ "customerId": 9001, "leadId": 12345, "selectionRequired": true, "attachments": [ { "attachmentId": 5001, "name": "A.pdf" }, { "attachmentId": 5002, "name": "B.pdf" } ] }""";

    private static VerificationTestingOptions On() =>
        new() { Enabled = true, SmsOverride = "971564114806", EmailOverride = TestEmail, BlockRealCustomerDelivery = true };

    [Theory]
    [InlineData("Contract")]
    [InlineData("ReservationForm")]
    [InlineData("RegistrationReceipt")]
    public async Task ThePdf_IsEmailedAsAnAttachment_ToTheTestMailboxOnly_WhenTestModeIsOn(string type)
    {
        var h = new Harness(On(), "UAT", One());

        var result = await h.Send(type);

        Assert.Equal(CrmDocumentCopyStatus.Sent, result.Status);
        var mail = Assert.Single(h.Mail.Sent);
        Assert.Equal(TestEmail, mail.ToAddress);
        var attachment = Assert.Single(mail.Attachments!);
        Assert.Equal(Pdf, attachment.Content);
        Assert.Equal("application/pdf", attachment.ContentType);
        Assert.EndsWith(".pdf", attachment.FileName);
        Assert.DoesNotContain("amazon", mail.Body + mail.Subject + (mail.HtmlBody ?? ""), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", mail.Body);                                     // no link in place of the file
        Assert.DoesNotContain(h.Mail.Sent, m => m.ToAddress == "customer@example.com");
        Assert.Contains("GetCustomerDocumentFile", h.CrmCalls.Single(c => c.Contains("File")));
    }

    [Fact]
    public async Task OutsideTestMode_ThePdfGoesToTheCustomersEmailOnRecord()
    {
        var h = new Harness(new VerificationTestingOptions(), "Production", One());

        var result = await h.Send();

        Assert.Equal(CrmDocumentCopyStatus.Sent, result.Status);
        var mail = Assert.Single(h.Mail.Sent);
        Assert.Equal("customer@example.com", mail.ToAddress);
        Assert.Equal(Pdf, Assert.Single(mail.Attachments!).Content);
    }

    [Fact]
    public async Task WhenSeveralDocumentsExist_NothingIsDownloadedOrSent_UntilTheCustomerChooses_ThenOnlyTheChosenOneIs()
    {
        var h = new Harness(On(), "UAT", Two);

        var ask = await h.Send();
        Assert.Equal(CrmDocumentCopyStatus.SelectionRequired, ask.Status);
        Assert.Equal(["5001", "5002"], ask.Choices!.Select(c => c.RecordId).ToArray());
        Assert.Empty(h.Mail.Sent);
        Assert.DoesNotContain(h.CrmCalls, c => c.Contains("GetCustomerDocumentFile"));

        var chosen = await h.Send(recordId: "5002", key: "k2");
        Assert.Equal(CrmDocumentCopyStatus.Sent, chosen.Status);
        Assert.Single(h.Mail.Sent);
    }

    [Fact]
    public async Task ARecordIdCrmDidNotListForThisCustomer_IsRefused_BeforeAnyDownload()
    {
        var h = new Harness(On(), "UAT", One());

        var result = await h.Send(recordId: "9999");

        Assert.Equal(CrmDocumentCopyStatus.OwnershipMismatch, result.Status);
        Assert.Empty(h.Mail.Sent);
        Assert.DoesNotContain(h.CrmCalls, c => c.Contains("GetCustomerDocumentFile"));
    }

    [Fact]
    public async Task ADocumentCrmCannotProvide_IsDocumentUnavailable_AndNothingIsSent()
    {
        var h = new Harness(On(), "UAT", One(), file: () => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await h.Send();

        Assert.Equal(CrmDocumentCopyStatus.DocumentUnavailable, result.Status);
        Assert.Empty(h.Mail.Sent);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ACrmFailure_IsReported_AndNothingIsSent(HttpStatusCode status)
    {
        var h = new Harness(On(), "UAT", One(), file: () => new HttpResponseMessage(status));

        var result = await h.Send();

        Assert.NotEqual(CrmDocumentCopyStatus.Sent, result.Status);
        Assert.Empty(h.Mail.Sent);
    }

    [Fact]
    public async Task ANonPdfFromCrm_IsNeverEmailed()
    {
        var h = new Harness(On(), "UAT", One(), file: () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("<html>login</html>")) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } }
        });

        var result = await h.Send();

        Assert.NotEqual(CrmDocumentCopyStatus.Sent, result.Status);
        Assert.Empty(h.Mail.Sent);
    }

    [Fact]
    public async Task SentIsRecordedOnlyWhenTheEmailProviderAccepts_AndARetryWithTheSameKeyResends_ButNeverDoubleSends()
    {
        var h = new Harness(On(), "UAT", One());
        h.Mail.ThenTransient();

        var failed = await h.Send(key: "same");
        Assert.Equal(CrmDocumentCopyStatus.DeliveryFailed, failed.Status);
        Assert.True(failed.Retryable);
        Assert.DoesNotContain(h.Fixture.Deliveries.All, d => d.Status == DocumentDeliveryStatus.Sent);

        var retried = await h.Send(key: "same");
        Assert.Equal(CrmDocumentCopyStatus.Sent, retried.Status);

        var replay = await h.Send(key: "same");
        Assert.True(replay.Duplicate);
        Assert.Equal(2, h.Mail.Sent.Count);                 // the rejected attempt + the accepted one; the replay sent nothing
        Assert.Single(h.Fixture.Deliveries.All, d => d.Status == DocumentDeliveryStatus.Sent);
    }

    [Fact]
    public async Task InvalidTestModeConfiguration_BlocksBeforeCrmIsEvenAsked()
    {
        var h = new Harness(new VerificationTestingOptions { Enabled = true, BlockRealCustomerDelivery = true }, "UAT", One());

        var result = await h.Send();

        Assert.Equal(CrmDocumentCopyStatus.DeliveryFailed, result.Status);
        Assert.Empty(h.Mail.Sent);
        Assert.Empty(h.CrmCalls);
    }
}

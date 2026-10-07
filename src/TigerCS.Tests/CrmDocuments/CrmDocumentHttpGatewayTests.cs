using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Integrations.Modules.CrmIntegration;
using TigerCS.Tests.CustomerVerification.Fakes;

namespace TigerCS.Tests.CrmDocuments;

/// <summary>
/// <see cref="CrmDocumentHttpGateway"/>'s contract with Tiger CRM's
/// <c>POST /TicketingSystem/GetCustomerDocuments</c> and with the storage
/// references it returns: the exact request, every response status, and the
/// rules that keep the CRM credential on the CRM origin.
/// </summary>
public class CrmDocumentHttpGatewayTests
{
    private const string BaseUrl = "https://crm.example.test:8014/";
    private const string Secret = "test-only-secret";

    private static CrmDocumentHttpGateway Gateway(
        StubHttpMessageHandler handler, string? secret = Secret, Action<CrmGatewayOptions>? configure = null)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(5) };
        var opts = new CrmGatewayOptions { BaseUrl = BaseUrl, SecretKey = secret };
        configure?.Invoke(opts);
        return new CrmDocumentHttpGateway(client, Options.Create(opts), NullLogger<CrmDocumentHttpGateway>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static StubHttpMessageHandler Answer(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(Json(status, json)));

    /// <summary>Answers like <see cref="Answer"/> but captures the request body — the content is disposed once the request completes.</summary>
    private static StubHttpMessageHandler Capturing(string json, Action<string> capture) =>
        new(async (request, _) =>
        {
            capture(await request.Content!.ReadAsStringAsync());
            return Json(HttpStatusCode.OK, json);
        });

    private const string TwoContracts = """
        { "customerId": 9001, "leadId": 12345, "count": 2, "selectionRequired": true,
          "attachments": [
            { "attachmentId": 5001, "fileUrl": "/Uploads/5001.pdf", "name": "Sale and Purchase Agreement.pdf" },
            { "attachmentId": "5002", "fileUrl": "~/Uploads/5002.pdf", "name": "Addendum 1.pdf" } ] }
        """;

    // ---- request ----

    [Fact]
    public async Task SendsTheDocumentedRequest_WithTheServerSideSecret()
    {
        string? sent = null;
        var handler = Capturing(TwoContracts, b => sent = b);

        await Gateway(handler).ListAsync(CrmDocumentType.Contract, 9001, 12345);

        var request = handler.LastRequest!;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://crm.example.test:8014/TicketingSystem/GetCustomerDocuments", request.RequestUri!.ToString());
        Assert.Equal(Secret, request.Headers.GetValues("X-SECRET-KEY").Single());
        using var body = JsonDocument.Parse(sent!);
        Assert.Equal(9001, body.RootElement.GetProperty("CustomerID").GetInt32());
        Assert.Equal(12345, body.RootElement.GetProperty("LeadID").GetInt32());
        Assert.Equal("TigerContract", body.RootElement.GetProperty("DocumentType").GetString());
        Assert.Equal(3, body.RootElement.EnumerateObject().Count()); // nothing else is sent
    }

    [Theory]
    [InlineData(CrmDocumentType.ReservationForm, "ReservationForm")]
    [InlineData(CrmDocumentType.Contract, "TigerContract")]
    [InlineData(CrmDocumentType.RegistrationReceipt, "RegistrationReceipt")]
    [InlineData(CrmDocumentType.UnitLayout, "Layout")]
    public async Task EveryPublicType_IsSentAsItsCrmName(CrmDocumentType type, string crmName)
    {
        string? sent = null;
        var handler = Capturing("""{ "customerId": 9001, "leadId": 12345, "count": 0, "selectionRequired": false, "attachments": [] }""", b => sent = b);

        await Gateway(handler).ListAsync(type, 9001, 12345);

        using var body = JsonDocument.Parse(sent!);
        Assert.Equal(crmName, body.RootElement.GetProperty("DocumentType").GetString());
    }

    [Fact]
    public async Task WithoutASecretKey_NoRequestIsMade()
    {
        var handler = Answer(TwoContracts);

        var ex = await Assert.ThrowsAsync<CrmDocumentSourceException>(() => Gateway(handler, secret: " ").ListAsync(CrmDocumentType.Contract, 1, 2));

        Assert.Equal(CrmDocumentSourceFailure.AuthenticationFailed, ex.Failure);
        Assert.Equal(0, handler.CallCount);
    }

    // ---- 200 ----

    [Fact]
    public async Task ParsesAttachments_AcceptingNumericAndStringAttachmentIds()
    {
        var listing = await Gateway(Answer(TwoContracts)).ListAsync(CrmDocumentType.Contract, 9001, 12345);

        Assert.True(listing.SelectionRequired);
        Assert.Equal(["5001", "5002"], listing.Records.Select(r => r.RecordId).ToArray());
        Assert.Equal("Addendum 1.pdf", listing.Records[1].Label);
        Assert.Equal("~/Uploads/5002.pdf", listing.Records[1].FileReference);
    }

    [Fact]
    public async Task Layout_HasNoAttachmentId_SoItsRecordIdComesFromTheLead()
    {
        var listing = await Gateway(Answer("""
            { "customerId": 9001, "leadId": 12345, "count": 1, "selectionRequired": false,
              "attachments": [ { "fileUrl": "/Uploads/plan.png", "name": "Unit plan" } ] }
            """)).ListAsync(CrmDocumentType.UnitLayout, 9001, 12345);

        var layout = Assert.Single(listing.Records);
        Assert.Equal("LAYOUT-12345", layout.RecordId);
        Assert.False(listing.SelectionRequired);
    }

    [Fact]
    public async Task AnAttachmentWithoutAnId_IsAnInvalidResponse_ForNonLayoutTypes()
    {
        var ex = await Assert.ThrowsAsync<CrmDocumentSourceException>(() => Gateway(Answer("""
            { "customerId": 9001, "leadId": 12345, "count": 1, "selectionRequired": false,
              "attachments": [ { "fileUrl": "/x.pdf", "name": "x" } ] }
            """)).ListAsync(CrmDocumentType.Contract, 9001, 12345));

        Assert.Equal(CrmDocumentSourceFailure.InvalidResponse, ex.Failure);
    }

    [Theory]
    [InlineData("""{ "customerId": 9002, "leadId": 12345, "count": 0, "selectionRequired": false, "attachments": [] }""")]
    [InlineData("""{ "customerId": 9001, "leadId": 99999, "count": 0, "selectionRequired": false, "attachments": [] }""")]
    [InlineData("""{ "count": 0, "selectionRequired": false, "attachments": [] }""")]
    [InlineData("""{ "success": false, "message": "nope" }""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("not json at all")]
    public async Task AResponseNotForThisCustomerAndLead_OrNotTheContract_IsRefused(string json)
    {
        var ex = await Assert.ThrowsAsync<CrmDocumentSourceException>(() => Gateway(Answer(json)).ListAsync(CrmDocumentType.Contract, 9001, 12345));

        Assert.Equal(CrmDocumentSourceFailure.InvalidResponse, ex.Failure);
    }

    // ---- statuses ----

    [Fact]
    public async Task A404_IsAnEmptyList_NotAFailure()
    {
        var listing = await Gateway(Answer("", HttpStatusCode.NotFound)).ListAsync(CrmDocumentType.Contract, 9001, 12345);

        Assert.Empty(listing.Records);
        Assert.False(listing.SelectionRequired);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, CrmDocumentSourceFailure.RequestRejected)]
    [InlineData(HttpStatusCode.Unauthorized, CrmDocumentSourceFailure.AuthenticationFailed)]
    [InlineData(HttpStatusCode.Forbidden, CrmDocumentSourceFailure.AccessDenied)]
    [InlineData(HttpStatusCode.InternalServerError, CrmDocumentSourceFailure.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CrmDocumentSourceFailure.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, CrmDocumentSourceFailure.Unavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout, CrmDocumentSourceFailure.Unavailable)]
    [InlineData(HttpStatusCode.Redirect, CrmDocumentSourceFailure.ReferenceRejected)]
    public async Task EachErrorStatus_MapsToItsOwnFailure(HttpStatusCode status, CrmDocumentSourceFailure expected)
    {
        var ex = await Assert.ThrowsAsync<CrmDocumentSourceException>(() => Gateway(Answer("{}", status)).ListAsync(CrmDocumentType.Contract, 9001, 12345));

        Assert.Equal(expected, ex.Failure);
    }

    [Fact]
    public async Task ATimeoutAndAConnectionFailure_AreUnavailable()
    {
        var timeout = new StubHttpMessageHandler((_, _) => throw new TaskCanceledException("timeout"));
        var refused = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("refused"));

        Assert.Equal(CrmDocumentSourceFailure.Unavailable,
            (await Assert.ThrowsAsync<CrmDocumentSourceException>(() => Gateway(timeout).ListAsync(CrmDocumentType.Contract, 1, 2))).Failure);
        Assert.Equal(CrmDocumentSourceFailure.Unavailable,
            (await Assert.ThrowsAsync<CrmDocumentSourceException>(() => Gateway(refused).ListAsync(CrmDocumentType.Contract, 1, 2))).Failure);
    }

    // ---- fileUrl is a storage reference, retrieved with the credential, on the CRM origin only ----

    private static StubHttpMessageHandler File(byte[] bytes, string contentType = "application/pdf", HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue(contentType) } }
        }));

    [Theory]
    [InlineData("/Uploads/a.pdf", "https://crm.example.test:8014/Uploads/a.pdf")]
    [InlineData("~/Uploads/a.pdf", "https://crm.example.test:8014/Uploads/a.pdf")]
    [InlineData("Uploads/a.pdf", "https://crm.example.test:8014/Uploads/a.pdf")]
    [InlineData("https://crm.example.test:8014/Uploads/a.pdf", "https://crm.example.test:8014/Uploads/a.pdf")]
    public async Task AReferenceOnTheCrmOrigin_IsFetchedWithTheSecret(string reference, string expectedUrl)
    {
        var handler = File([1, 2, 3]);

        var content = await Gateway(handler).DownloadAsync(new CrmDocumentRecord("5001", "Contract", reference));

        Assert.Equal(expectedUrl, handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
        Assert.Equal(Secret, handler.LastRequest.Headers.GetValues("X-SECRET-KEY").Single());
        Assert.Equal([1, 2, 3], content!.Bytes);
        Assert.Equal("5001", content.RecordId);
    }

    [Theory]
    [InlineData("https://evil.example.net/a.pdf")]
    [InlineData("http://crm.example.test:8014/a.pdf")]      // same host, different scheme
    [InlineData("https://crm.example.test:9999/a.pdf")]     // same host, different port
    [InlineData("https://crm.example.test.evil.net/a.pdf")] // look-alike host
    [InlineData("//evil.example.net/a.pdf")]                // protocol-relative
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://crm.example.test/a.pdf")]
    [InlineData("javascript:alert(1)")]
    public async Task AReferenceOffTheCrmOrigin_IsRefused_AndNoRequestIsMade(string reference)
    {
        var handler = File([1]);

        var ex = await Assert.ThrowsAsync<CrmDocumentSourceException>(
            () => Gateway(handler).DownloadAsync(new CrmDocumentRecord("5001", "Contract", reference)));

        Assert.Equal(CrmDocumentSourceFailure.ReferenceRejected, ex.Failure);
        Assert.Equal(0, handler.CallCount); // the secret never left
    }

    [Fact]
    public async Task AnAllowListedHttpsFileHost_IsFetched()
    {
        var handler = File([7]);

        var content = await Gateway(handler, configure: o => o.DocumentFileHosts.Add("files.example.test"))
            .DownloadAsync(new CrmDocumentRecord("5001", "Contract", "https://files.example.test/a.pdf"));

        Assert.Equal("https://files.example.test/a.pdf", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal([7], content!.Bytes);
    }

    [Fact]
    public async Task ARedirectIsNotFollowed_AndAnHtmlPageIsNotADocument()
    {
        var redirect = new StubHttpMessageHandler((_, _) =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://evil.example.net/x");
            return Task.FromResult(r);
        });
        Assert.Equal(CrmDocumentSourceFailure.ReferenceRejected,
            (await Assert.ThrowsAsync<CrmDocumentSourceException>(
                () => Gateway(redirect).DownloadAsync(new CrmDocumentRecord("1", "x", "/a.pdf")))).Failure);

        var login = File(Encoding.UTF8.GetBytes("<html>Login</html>"), "text/html");
        Assert.Equal(CrmDocumentSourceFailure.InvalidResponse,
            (await Assert.ThrowsAsync<CrmDocumentSourceException>(
                () => Gateway(login).DownloadAsync(new CrmDocumentRecord("1", "x", "/a.pdf")))).Failure);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CrmDocumentSourceFailure.AuthenticationFailed)]
    [InlineData(HttpStatusCode.Forbidden, CrmDocumentSourceFailure.AccessDenied)]
    [InlineData(HttpStatusCode.InternalServerError, CrmDocumentSourceFailure.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CrmDocumentSourceFailure.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, CrmDocumentSourceFailure.RequestRejected)]
    public async Task DownloadErrorStatuses_MapToTheirFailure(HttpStatusCode status, CrmDocumentSourceFailure expected)
    {
        var ex = await Assert.ThrowsAsync<CrmDocumentSourceException>(
            () => Gateway(File([], status: status)).DownloadAsync(new CrmDocumentRecord("1", "x", "/a.pdf")));

        Assert.Equal(expected, ex.Failure);
    }

    [Fact]
    public async Task AMissingFile_IsNull()
    {
        Assert.Null(await Gateway(File([], status: HttpStatusCode.NotFound)).DownloadAsync(new CrmDocumentRecord("1", "x", "/a.pdf")));
    }

    [Fact]
    public async Task ABodyLargerThanTheCap_IsReadOnlyToTheCapPlusOne()
    {
        var content = await Gateway(File(new byte[5000]), configure: o => o.MaxDocumentBytes = 100)
            .DownloadAsync(new CrmDocumentRecord("1", "x", "/a.pdf"));

        Assert.Equal(101, content!.Bytes.Length); // enough for the service to see "too large"
    }

    [Theory]
    [InlineData("Sale and Purchase Agreement.pdf", "/Uploads/1.pdf", "application/pdf", "Sale and Purchase Agreement.pdf")]
    [InlineData("Contract", "/Uploads/1.pdf", "application/octet-stream", "Contract.pdf")]
    [InlineData("../../etc/passwd", "/Uploads/1", "application/pdf", "passwd.pdf")]
    [InlineData("a\"b;c\r\nd.pdf", "/x/1", "application/pdf", "a_b_c__d.pdf")]
    [InlineData("", "/x/plan.png", "image/png", "document.png")]
    public async Task TheAttachmentFileName_IsSanitisedAndGivenAnExtension(string label, string url, string contentType, string expected)
    {
        var content = await Gateway(File([1], contentType)).DownloadAsync(new CrmDocumentRecord("1", label, url));

        Assert.Equal(expected, content!.FileName);
    }
}

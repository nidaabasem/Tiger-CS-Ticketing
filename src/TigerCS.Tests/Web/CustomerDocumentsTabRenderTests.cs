// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CrmDocuments.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCsWeb::TigerCS.Web.Models;
using TigerCsWeb::TigerCS.Web.Services.Api;

namespace TigerCS.Tests.Web;

/// <summary>
/// The Customer Profile's Documents tab through the real Razor pipeline against a fake TigerCS.Api: the loading
/// placeholder, the listing (name, type, Open / Download, Send to customer), the unit selector and its fragment, every
/// empty / error / unavailable state, the file relay, and the send round trip with its wording.
/// </summary>
public sealed class CustomerDocumentsTabRenderTests : IDisposable
{
    private static readonly Guid ViewerId = Guid.NewGuid();
    private static readonly DateTime Now = DateTime.UtcNow;
    private readonly FakeApi _api = new();
    private readonly RoleBox _role = new();
    private readonly WebApplicationFactory<TigerCsWeb::Program> _factory;

    public CustomerDocumentsTabRenderTests()
    {
        _factory = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(_role);
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = _api));
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                });
            });
        });
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient Client() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<string> Ok(HttpResponseMessage response)
    {
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}\n{html[..Math.Min(html.Length, 2000)]}");
        return html;
    }

    // ---------------------------------------------------------------- the tab and its loading state

    [Fact]
    public async Task TheProfile_HasADocumentsTab_ThatIsLoadedLazily_AndShowsALoadingState()
    {
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001"));

        Assert.Contains("for=\"tab-documents\">Documents</label>", html, StringComparison.Ordinal);
        Assert.Contains("data-documents-src=\"/Customers/crm%3A9001?handler=DocumentsPanel\"", html, StringComparison.Ordinal);
        Assert.Contains("data-documents-state=\"Deferred\"", html, StringComparison.Ordinal);
        Assert.Contains("Loading documents…", html, StringComparison.Ordinal);
        Assert.DoesNotContain(_api.Requests, r => r.Contains("/documents", StringComparison.Ordinal)); // nothing fetched until the tab opens
        Assert.DoesNotContain("Documents</label>\r\n            <input type=\"radio\" name=\"customerProfileTab\" id=\"tab-documents\" class=\"tab-input\" checked", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASingleUnit_ListsItsDocuments_WithNameTypeOpenDownloadAndSend()
    {
        _api.Units = [Unit1];
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=documents"));

        Assert.Contains("id=\"tab-documents\" class=\"tab-input\" checked", html, StringComparison.Ordinal);
        Assert.Contains("data-documents-state=\"Loaded\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-documents-selector", html, StringComparison.Ordinal); // nothing to select
        Assert.Contains("SPA 1205.pdf", html, StringComparison.Ordinal);
        Assert.Contains("Reservation Form 1205.pdf", html, StringComparison.Ordinal);
        Assert.Contains("CRM type 4", html, StringComparison.Ordinal);
        Assert.Contains("CRM type 5", html, StringComparison.Ordinal);
        Assert.Contains("CRM type 6", html, StringComparison.Ordinal);
        Assert.Contains(">Open</a>", html, StringComparison.Ordinal);
        Assert.Contains(">Download</a>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Customers/crm%3A9001?handler=DocumentFile&amp;unit=12345&amp;type=Contract&amp;record=5001\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Customers/crm%3A9001?handler=DocumentFile&amp;unit=12345&amp;type=Contract&amp;record=5001&amp;download=true\"", html, StringComparison.Ordinal);
        Assert.Contains("a***@e***.test", html, StringComparison.Ordinal);
        Assert.Contains("data-documents-empty", html, StringComparison.Ordinal); // the registration receipt group is empty
        Assert.Contains("No registration receipt on record for this unit.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/Uploads/", html, StringComparison.Ordinal); // CRM's storage reference is never in the page
    }

    [Fact]
    public async Task SendToCustomer_IsEnabledPerDocument_WhenAvailable_AndEachFormCarriesItsOwnIdempotencyKey()
    {
        _api.Units = [Unit1];
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=documents"));

        var buttons = Regex.Matches(html, "<button type=\"submit\"[^>]*data-document-send>Send to customer</button>");
        Assert.Equal(3, buttons.Count);
        Assert.All(buttons, b => Assert.DoesNotContain("disabled", b.Value, StringComparison.Ordinal));
        var keys = Regex.Matches(html, "name=\"idempotencyKey\" value=\"([0-9a-f]{32})\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(3, keys.Distinct().Count());
        Assert.Contains("name=\"__RequestVerificationToken\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-documents-send-unavailable", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenSendingIsUnavailable_EveryButtonIsDisabled_AndTheReasonIsShown_ButFilesStillOpen()
    {
        _api.Units = [Unit1];
        _api.SendReason = "CRM holds no valid email address for this customer. Documents are sent only to the address CRM holds.";
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=documents"));

        Assert.Contains("data-documents-send-unavailable", html, StringComparison.Ordinal);
        Assert.Contains("Send to customer is unavailable.", html, StringComparison.Ordinal);
        Assert.Contains("CRM holds no valid email address", html, StringComparison.Ordinal);
        var buttons = Regex.Matches(html, "<button type=\"submit\"[^>]*data-document-send>");
        Assert.Equal(3, buttons.Count);
        Assert.All(buttons, b => Assert.Contains("disabled", b.Value, StringComparison.Ordinal));
        Assert.Contains(">Open</a>", html, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- units

    [Fact]
    public async Task SeveralUnits_AndNoneChosen_ShowTheSelectorAndNoDocuments()
    {
        _api.Units = [Unit1, Unit2];
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=documents"));

        Assert.Contains("data-documents-selector", html, StringComparison.Ordinal);
        Assert.Contains("name=\"tab\" value=\"documents\"", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"\" selected>Select a unit…</option>", html, StringComparison.Ordinal);
        Assert.Contains("1205 · Tiger Sky Tower", WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        Assert.Contains("1403 · Tiger Sky Tower", WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        Assert.Contains("data-documents-select-unit", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-document-row", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheUnitFragment_ContainsOnlyTheChosenUnitsDocuments_AndTheSelectionIsKept()
    {
        _api.Units = [Unit1, Unit2];
        var client = Client();

        var first = await Ok(await client.GetAsync("/Customers/crm:9001?handler=DocumentsPanel&unit=12345"));
        var second = await Ok(await client.GetAsync("/Customers/crm:9001?handler=DocumentsPanel&unit=12346"));

        Assert.StartsWith("<div class=\"documents-tab\"", first.TrimStart(), StringComparison.Ordinal); // a fragment, not a page
        Assert.Contains("data-documents-unit=\"12345\"", first, StringComparison.Ordinal);
        Assert.Contains("SPA 1205.pdf", first, StringComparison.Ordinal);
        Assert.Contains("<option value=\"12345\" selected=\"selected\">", first, StringComparison.Ordinal);

        Assert.Contains("data-documents-unit=\"12346\"", second, StringComparison.Ordinal);
        Assert.Contains("SPA 1403.pdf", second, StringComparison.Ordinal);
        Assert.Contains("Receipt 1403.pdf", second, StringComparison.Ordinal);
        Assert.DoesNotContain("SPA 1205.pdf", second, StringComparison.Ordinal);
        Assert.DoesNotContain("Reservation Form 1205.pdf", second, StringComparison.Ordinal);
        Assert.DoesNotContain("unit=12345", second, StringComparison.Ordinal); // no link to the previous unit's files
        Assert.Contains("&amp;unit=12346&amp;", second, StringComparison.Ordinal);
        Assert.Contains(_api.Requests, r => r.EndsWith("/documents?crmLeadId=12346", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- states

    [Theory]
    [InlineData("NotCrmCustomer", "Documents are held in Tiger CRM")]
    [InlineData("NoUnits", "CRM lists no eligible unit")]
    [InlineData("CustomerNotResolved", "CRM does not resolve any phone")]
    public async Task EmptyStates_SayWhy(string status, string text)
    {
        _api.Status = status;
        _api.StatusMessage = text + " (detail).";
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=documents"));

        Assert.Contains(text, html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-document-row", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "CRM could not be reached", "data-documents-error")]
    [InlineData(HttpStatusCode.BadGateway, "CRM rejected TigerCS's credential", "data-documents-error")]
    [InlineData(HttpStatusCode.InternalServerError, "", "data-documents-error")]
    public async Task ARetrievalError_IsShownWithRetry_AndNoDocuments(HttpStatusCode status, string detail, string marker)
    {
        _api.Units = [Unit1];
        _api.ListFailure = (status, detail);
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=documents"));

        Assert.Contains(marker, html, StringComparison.Ordinal);
        Assert.Contains("Documents could not be retrieved.", html, StringComparison.Ordinal);
        Assert.Contains("data-documents-retry>Retry</a>", html, StringComparison.Ordinal);
        if (detail.Length > 0) Assert.Contains(detail, WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        Assert.DoesNotContain("data-document-row", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForbiddenUnit_ShowsNoDocuments()
    {
        _api.Units = [Unit1];
        _api.ListFailure = (HttpStatusCode.Forbidden, "That unit is not one of this customer's units.");
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=documents&unit=99999"));

        Assert.Contains("That unit is not one of this customer&#x27;s units.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-document-row", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneGroupFailing_ShowsItsOwnError_WhileTheOthersStillList()
    {
        _api.Units = [Unit1];
        _api.FailGroup = "Contract";
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=documents"));

        Assert.Contains("data-documents-group-error", html, StringComparison.Ordinal);
        Assert.Contains("Tiger Contract could not be retrieved.", html, StringComparison.Ordinal);
        Assert.Contains("Reservation Form 1205.pdf", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReadOnlyViewer_CanOpenButNotSend()
    {
        _api.Units = [Unit1];
        _role.Role = Roles.ReportingUser;
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=documents"));

        Assert.Contains("Your role is read-only", html, StringComparison.Ordinal);
        Assert.All(Regex.Matches(html, "<button type=\"submit\"[^>]*data-document-send>"), b => Assert.Contains("disabled", b.Value, StringComparison.Ordinal));
        Assert.Contains(">Open</a>", html, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- Open / Download

    [Fact]
    public async Task OpenAndDownload_RelayTheApisFile_WithItsTypeAndDisposition()
    {
        _api.Units = [Unit1];
        var client = Client();

        var open = await client.GetAsync("/Customers/crm:9001?handler=DocumentFile&unit=12345&type=Contract&record=5001");
        var download = await client.GetAsync("/Customers/crm:9001?handler=DocumentFile&unit=12345&type=Contract&record=5001&download=true");

        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.Equal("application/pdf", open.Content.Headers.ContentType?.MediaType);
        Assert.Equal(FakeApi.FileBytes("5001"), await open.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("inline", open.Content.Headers.ContentDisposition?.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("attachment", download.Content.Headers.ContentDisposition?.ToString(), StringComparison.Ordinal);
        Assert.Contains("SPA 1205.pdf", Uri.UnescapeDataString(download.Content.Headers.ContentDisposition!.ToString()), StringComparison.Ordinal);
        Assert.Equal("nosniff", open.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("no-store", open.Headers.CacheControl?.ToString());
        Assert.Contains(_api.Requests, r => r == "/api/customers/crm%3A9001/documents/12345/Contract/5001/file");
        Assert.Contains(_api.Requests, r => r == "/api/customers/crm%3A9001/documents/12345/Contract/5001/file?download=true");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadGateway, HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task AFileTheApiRefuses_IsAClearErrorPage_NeverABlankFile(HttpStatusCode api, HttpStatusCode web)
    {
        _api.FileFailure = api;
        var response = await Client().GetAsync("/Customers/crm:9001?handler=DocumentFile&unit=12345&type=Contract&record=5001");

        Assert.Equal(web, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
        Assert.NotEmpty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnIncompleteFileLink_IsA400_AndNothingIsAskedOfTheApi()
    {
        var response = await Client().GetAsync("/Customers/crm:9001?handler=DocumentFile&type=Contract");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(_api.Requests, r => r.Contains("/file", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- Send to customer

    private async Task<(HttpClient Client, HttpResponseMessage Post, string Page)> PostSendAsync()
    {
        _api.Units = [Unit1];
        var client = Client();
        var page = await Ok(await client.GetAsync("/Customers/crm:9001?tab=documents"));
        string Field(string name) => Regex.Match(page, $"name=\"{name}\" value=\"([^\"]*)\"").Groups[1].Value;
        var post = await client.PostAsync("/Customers/crm:9001?handler=SendDocument", new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value),
            new("unit", "12345"), new("type", "Contract"), new("record", "5002"), new("documentName", "Addendum 1205.pdf"),
            new("idempotencyKey", Field("idempotencyKey"))
        ]));
        return (client, post, page);
    }

    [Fact]
    public async Task Send_ForwardsTheUnitTypeRecordAndTheFormsKey_AndReturnsToTheTab()
    {
        var (_, post, page) = await PostSendAsync();

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal("/Customers/crm%3A9001?tab=documents&unit=12345#documents", post.Headers.Location!.OriginalString);
        var sent = JsonSerializer.Deserialize<CustomerDocumentSendRequestDto>(_api.LastPostBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(12345, sent.CrmLeadId);
        Assert.Equal("Contract", sent.DocumentType);
        Assert.Equal("5002", sent.RecordId);
        Assert.Equal(Regex.Match(page, "name=\"idempotencyKey\" value=\"([0-9a-f]{32})\"").Groups[1].Value, _api.LastIdempotencyKey);
        Assert.DoesNotContain("@", _api.LastPostBody); // the request never names an address
    }

    [Fact]
    public async Task Send_Accepted_SaysAcceptedForSending_AndNeverClaimsDelivery()
    {
        var (client, post, _) = await PostSendAsync();

        var back = await Ok(await client.GetAsync(post.Headers.Location));

        Assert.Contains("data-documents-notice", back, StringComparison.Ordinal);
        Assert.Contains("Accepted for sending: “Addendum 1205.pdf” was handed to the mail server to a***@e***.test as an attachment.", WebUtility.HtmlDecode(back), StringComparison.Ordinal);
        Assert.Contains("Accepted is not delivered", back, StringComparison.Ordinal);
        Assert.DoesNotContain("Document delivered", back, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("successfully delivered", back, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "DOCUMENT_COPY_DISABLED", "Sending document copies is switched off", "was not sent")]
    [InlineData(HttpStatusCode.BadGateway, "DELIVERY_FAILED", "The document could not be delivered right now.", "was not sent")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "DELIVERY_DESTINATION_UNAVAILABLE", "CRM has no valid email address", "was not sent")]
    [InlineData(HttpStatusCode.Forbidden, "RECORD_OWNERSHIP_MISMATCH", "That record does not belong", "was not sent")]
    public async Task Send_Failures_AreShownAsNotSent_WithTheServicesOwnReason(HttpStatusCode status, string code, string detail, string lead)
    {
        _api.SendFailure = (status, code, detail);
        var (client, post, _) = await PostSendAsync();

        var back = await Ok(await client.GetAsync(post.Headers.Location));

        Assert.Contains(lead, back, StringComparison.Ordinal);
        Assert.Contains(detail, back, StringComparison.Ordinal);
        Assert.Contains("alert-error", back, StringComparison.Ordinal);
        Assert.DoesNotContain("Accepted for sending", back, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_ARepeatedRequest_IsReportedAsNotSentAgain()
    {
        _api.SendDuplicate = true;
        var (client, post, _) = await PostSendAsync();

        var back = await Ok(await client.GetAsync(post.Headers.Location));

        Assert.Contains("Not sent again: “Addendum 1205.pdf” was already sent to a***@e***.test", WebUtility.HtmlDecode(back), StringComparison.Ordinal);
        Assert.DoesNotContain("Accepted for sending", back, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the words

    [Fact]
    public void SendNotice_FollowsTheEvidence()
    {
        static ApiResult<CrmDocumentCopyResult> R(CrmDocumentCopyStatus s, bool duplicate = false) =>
            ApiResult<CrmDocumentCopyResult>.Success(new CrmDocumentCopyResult(s, MaskedDestination: "a***@e***.test", Duplicate: duplicate, Message: "m"));

        var sent = CustomerDocumentsPanel.SendNotice(R(CrmDocumentCopyStatus.Sent), "X.pdf");
        Assert.False(sent.IsError);
        Assert.StartsWith("Accepted for sending", sent.Message, StringComparison.Ordinal);
        Assert.Contains("not delivered", sent.Message, StringComparison.Ordinal);

        var again = CustomerDocumentsPanel.SendNotice(R(CrmDocumentCopyStatus.Sent, duplicate: true), "X.pdf");
        Assert.StartsWith("Not sent again", again.Message, StringComparison.Ordinal);

        var queued = CustomerDocumentsPanel.SendNotice(R(CrmDocumentCopyStatus.Queued), "X.pdf");
        Assert.Contains("Nothing new was sent", queued.Message, StringComparison.Ordinal);
        Assert.False(queued.IsError);

        var selection = CustomerDocumentsPanel.SendNotice(R(CrmDocumentCopyStatus.SelectionRequired), "X.pdf");
        Assert.True(selection.IsError); // nothing was sent

        var unreachable = CustomerDocumentsPanel.SendNotice(ApiResult<CrmDocumentCopyResult>.Failure(ApiOutcome.Unreachable), "X.pdf");
        Assert.True(unreachable.IsError);
        Assert.Contains("Nothing is known to have been sent", unreachable.Message, StringComparison.Ordinal);

        foreach (var notice in new[] { sent, again, queued, selection, unreachable })
        {
            Assert.DoesNotContain("has been delivered", notice.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("was delivered", notice.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- hosts

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Context.RequestServices.GetRequiredService<RoleBox>().Role;
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, ViewerId.ToString()),
                new Claim(ClaimTypes.Name, "Test Supervisor"),
                new Claim(ClaimTypes.Role, role),
            ], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    /// <summary>The role the signed-in test viewer holds.</summary>
    private sealed class RoleBox
    {
        public string Role { get; set; } = Roles.CsSupervisor;
    }

    private static readonly CustomerDocumentUnitDto Unit1 = new(12345, "1205", "Tiger Sky Tower", "Sold");
    private static readonly CustomerDocumentUnitDto Unit2 = new(12346, "1403", "Tiger Sky Tower", "Sold");

    private sealed class FakeApi : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public IReadOnlyList<CustomerDocumentUnitDto> Units { get; set; } = [];
        public string Status { get; set; } = "Ready";
        public string? StatusMessage { get; set; }
        public string? SendReason { get; set; }
        public (HttpStatusCode Status, string Detail)? ListFailure { get; set; }
        public string? FailGroup { get; set; }
        public HttpStatusCode? FileFailure { get; set; }
        public (HttpStatusCode Status, string Code, string Detail)? SendFailure { get; set; }
        public bool SendDuplicate { get; set; }
        public string? LastPostBody { get; private set; }
        public string? LastIdempotencyKey { get; private set; }

        public static byte[] FileBytes(string id) => Encoding.ASCII.GetBytes($"%PDF-1.7\nrecord-{id}\n");

        private static readonly CustomerDirectoryProfileDto Profile = new(
            "crm:9001", "Crm", "Test Buyer", ["+971500000900"], [], "Crm", 9001, null, null, 0, 1, Now.AddDays(-30), Now.AddDays(-1), 6, [], [], []);

        private CustomerDocumentsDto Listing(int? lead)
        {
            var selected = lead ?? (Units.Count == 1 ? Units[0].LeadId : null);
            if (Status != "Ready")
            {
                return new CustomerDocumentsDto(Status, StatusMessage, [], null, [], new CustomerDocumentSendDto(false, StatusMessage, null));
            }

            if (selected is null)
            {
                return new CustomerDocumentsDto("UnitSelectionRequired", "This customer has more than one unit. Select a unit to see its documents.", Units, null, [],
                    new CustomerDocumentSendDto(false, "Select a unit to see its documents.", null));
            }

            var unit1 = selected == 12345;
            CustomerDocumentGroupDto G(string type, string label, int crm, params CustomerDocumentDto[] docs) =>
                type == FailGroup
                    ? new CustomerDocumentGroupDto(type, label, crm, "Unavailable", "CRM's document source is unavailable right now. Try again shortly.", [])
                    : new CustomerDocumentGroupDto(type, label, crm, "Ok", null, docs);
            return new CustomerDocumentsDto("Ready", null, Units, selected,
            [
                G("ReservationForm", "Reservation Form", 4, new CustomerDocumentDto(unit1 ? "4001" : "4002", unit1 ? "Reservation Form 1205.pdf" : "Reservation Form 1403.pdf")),
                unit1
                    ? G("Contract", "Tiger Contract", 5, new CustomerDocumentDto("5001", "SPA 1205.pdf"), new CustomerDocumentDto("5002", "Addendum 1205.pdf"))
                    : G("Contract", "Tiger Contract", 5, new CustomerDocumentDto("5003", "SPA 1403.pdf")),
                unit1
                    ? G("RegistrationReceipt", "Registration Receipt", 6)
                    : G("RegistrationReceipt", "Registration Receipt", 6, new CustomerDocumentDto("6001", "Receipt 1403.pdf"))
            ],
            new CustomerDocumentSendDto(SendReason is null, SendReason, "a***@e***.test"));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            var path = Uri.UnescapeDataString(request.RequestUri.AbsolutePath);
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);

            if (path == "/api/customers/profile/crm:9001")
            {
                return Json(HttpStatusCode.OK, Profile);
            }

            if (path.EndsWith("/documents/send", StringComparison.Ordinal))
            {
                LastPostBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                LastIdempotencyKey = request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null;
                if (SendFailure is { } f)
                {
                    return Problem(f.Status, f.Code, f.Detail);
                }

                return Json(HttpStatusCode.OK, new CrmDocumentCopyResult(
                    CrmDocumentCopyStatus.Sent, Message: "sent", DocumentType: "Contract", RecordId: "5002", DeliveryChannel: "Email",
                    MaskedDestination: "a***@e***.test", DeliveryRequestId: 1, Duplicate: SendDuplicate));
            }

            if (path.EndsWith("/file", StringComparison.Ordinal))
            {
                if (FileFailure is { } failure)
                {
                    return Problem(failure, "X", "refused");
                }

                var id = path.Split('/')[^2];
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(FileBytes(id)) };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
                response.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue(query["download"] == "true" ? "attachment" : "inline")
                { FileNameStar = "SPA 1205.pdf" };
                return response;
            }

            if (path.EndsWith("/documents", StringComparison.Ordinal))
            {
                if (ListFailure is { } listFailure)
                {
                    return Problem(listFailure.Status, "X", listFailure.Detail);
                }

                return Json(HttpStatusCode.OK, Listing(int.TryParse(query["crmLeadId"], out var lead) ? lead : null));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = JsonContent.Create(body, body.GetType(), options: new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }) };

        private static HttpResponseMessage Problem(HttpStatusCode status, string code, string detail) =>
            new(status) { Content = JsonContent.Create(new { type = $"https://tigercs.internal/problems/{code.ToLowerInvariant()}", title = code, detail, code }) };
    }
}

// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Web;

/// <summary>
/// The Customer Profile's Payment tab through the real Razor pipeline against
/// a fake TigerCS.Api: every state (loading, loaded, empty, forbidden, stale,
/// switched off, source unavailable — never a zero), the lazily fetched
/// panel, and Send Reminder relayed through the Api.
/// </summary>
public sealed class CustomerPaymentTabRenderTests : IDisposable
{
    private static readonly Guid ViewerId = Guid.NewGuid();
    private static readonly DateTime Now = DateTime.UtcNow;
    private readonly FakeApi _api = new();
    private readonly WebApplicationFactory<TigerCsWeb::Program> _factory;

    public CustomerPaymentTabRenderTests()
    {
        _factory = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
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

    private bool CollectionsCalled => _api.Requests.Any(r => r.Contains("/api/genesys/collections", StringComparison.Ordinal));

    [Fact]
    public async Task ProfileOffersThePaymentTab_DeferredWithALoadingState_AndCallsNoFinancialRouteUpfront()
    {
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001"));

        Assert.Contains("<label class=\"tab-label\" for=\"tab-payment\">Payment</label>", html, StringComparison.Ordinal);
        Assert.Contains("data-payment-src=\"/Customers/crm%3A9001?handler=PaymentPanel\"", html, StringComparison.Ordinal);
        Assert.Contains("data-payment-state=\"Deferred\"", html, StringComparison.Ordinal);
        Assert.Contains("Loading payment details…", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Customers/crm%3A9001?tab=payment#payment\"", html, StringComparison.Ordinal);
        Assert.False(CollectionsCalled);
    }

    [Fact]
    public async Task LoadedTab_ShowsTheSourceFiguresInCurrency_SchedulePaymentsRemindersAndTickets()
    {
        _api.CanSend = true;
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment"));

        Assert.Matches(new Regex("id=\"tab-payment\"[^>]*checked"), html);
        Assert.Contains("data-payment-state=\"Loaded\"", html, StringComparison.Ordinal);
        Assert.Contains("AED 14,500.00", html, StringComparison.Ordinal);        // amount due now
        Assert.Contains("AED 44,000.00", html, StringComparison.Ordinal);        // remaining principal
        Assert.Contains("Amount due now", html, StringComparison.Ordinal);
        Assert.Contains("This month's remaining payment", html, StringComparison.Ordinal);
        Assert.Contains("Last updated", html, StringComparison.Ordinal);
        Assert.Contains("Currency <strong>AED</strong>", html, StringComparison.Ordinal);

        // Account selector, instalments, payments (the unverified one flagged), reminders with their ticket.
        Assert.Contains("name=\"account\"", html, StringComparison.Ordinal);
        Assert.Contains(">Partially paid<", html, StringComparison.Ordinal);
        Assert.Contains(">Pending verification<", html, StringComparison.Ordinal);
        Assert.Contains("Not counted in the balance", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Tickets/77\">TG-COL-00077</a>", html, StringComparison.Ordinal);
        Assert.Contains("Says already paid &#xB7; verification needed", html, StringComparison.Ordinal);

        // No receipt/SOA download without a verified document API.
        Assert.Contains("data-documents-unavailable", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Download", html, StringComparison.Ordinal);

        Assert.Contains(">Send Reminder</button>", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"Email\">Email</option>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<option value=\"VoiceBot\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendReminder_IsHiddenFromViewersWithoutThePermission_AndForSettledAccounts()
    {
        _api.CanSend = false;
        Assert.DoesNotContain("Send Reminder", await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment")), StringComparison.Ordinal);

        _api.CanSend = true;
        var settled = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment&account=ACC-9001-0805"));
        Assert.DoesNotContain("Send Reminder", settled, StringComparison.Ordinal);
        Assert.Contains("Not needed &#x2014; settled", settled, StringComparison.Ordinal);
        Assert.Contains("/payments?accountId=ACC-9001-0805", string.Join("\n", _api.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forbidden_ShowsNoFigures()
    {
        _api.Mode = "forbidden";
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment"));

        Assert.Contains("You don't have permission to view this customer's payment information.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("AED", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceUnavailable_SaysUnavailable_NeverZero_AndStillShowsReminderHistory()
    {
        _api.Mode = "unavailable";
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment"));

        Assert.Contains("Balance unavailable.", html, StringComparison.Ordinal);
        Assert.Contains("data-balance-unavailable", html, StringComparison.Ordinal);
        Assert.DoesNotContain("AED 0.00", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Amount due now", html, StringComparison.Ordinal);
        Assert.Contains("Reminder history", html, StringComparison.Ordinal);
        Assert.Contains("TG-COL-00077", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_SaysSo()
    {
        _api.Mode = "disabled";
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment"));

        Assert.Contains("Collections is not enabled in this environment", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Balance unavailable", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleFigures_AreFlagged()
    {
        _api.Stale = true;
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment"));

        Assert.Contains("data-balance-stale", html, StringComparison.Ordinal);
        Assert.Contains("These figures may be out of date", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoAccountsAtTheSource_IsAnEmptyState()
    {
        _api.Mode = "notfound";
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment"));

        Assert.Contains("The financial source has no payment accounts for this customer.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APhoneOnlyCaller_HasNoCrmAccountToLookUp()
    {
        var html = await Ok(await Client().GetAsync("/Customers/phone:%2B971501112222?tab=payment"));

        Assert.Contains("Payment information is available only for customers identified in Tiger CRM.", html, StringComparison.Ordinal);
        Assert.False(CollectionsCalled);
    }

    [Fact]
    public async Task PaymentPanelHandler_ReturnsTheTabAlone()
    {
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?handler=PaymentPanel"));

        Assert.StartsWith("<div class=\"payment-tab\" data-payment-state=\"Loaded\"", html.TrimStart(), StringComparison.Ordinal);
        Assert.DoesNotContain("<html", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendReminder_UsesTheProfilesCrmId_RelaysTheApiAnswer_AndReturnsToThePaymentTab()
    {
        _api.CanSend = true;
        var client = Client();
        var page = await Ok(await client.GetAsync("/Customers/crm:9001?tab=payment"));
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

        var post = await client.PostAsync("/Customers/crm:9001?handler=SendReminder", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["accountId"] = "ACC-9001-1204",
            ["channel"] = "Email",
        }));

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal("/Customers/crm%3A9001?tab=payment&account=ACC-9001-1204#payment", post.Headers.Location!.OriginalString);

        var sent = JsonSerializer.Deserialize<CreateCollectionsReminderRequestDto>(_api.LastPostBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(new CreateCollectionsReminderRequestDto("9001", "ACC-9001-1204", "Email", "Manual"), sent);

        var back = await Ok(await client.GetAsync(post.Headers.Location));
        Assert.Contains("Email reminder queued for AED 14,000.00.", back, StringComparison.Ordinal);
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, ViewerId.ToString()),
                new Claim(ClaimTypes.Name, "Test Supervisor"),
                new Claim(ClaimTypes.Role, Roles.CsSupervisor),
            ], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    private sealed class FakeApi : HttpMessageHandler
    {
        public string Mode { get; set; } = "loaded";
        public bool CanSend { get; set; }
        public bool Stale { get; set; }
        public List<string> Requests { get; } = [];
        public string? LastPostBody { get; private set; }

        private static readonly CustomerDirectoryProfileDto Profile = new(
            "crm:9001", "Crm", "Test Buyer", ["+971500000900"], [], "Crm", 9001, null, null, 0, 1, Now.AddDays(-30), Now.AddDays(-1), 6, [], [], []);

        private static readonly CustomerDirectoryProfileDto Caller = new(
            "phone:%2B971501112222", "Phone", null, ["+971501112222"], [], "Unverified", null, null, null, 0, 0, Now.AddDays(-3), Now.AddDays(-3), 7, [], [], []);

        private static readonly DateOnly Today = DateOnly.FromDateTime(Now);

        private CollectionsOutstandingResponseDto Outstanding()
        {
            var asOf = Stale ? Now.AddHours(-5) : Now.AddMinutes(-2);
            var arrears = new CollectionsAccountDto(
                "ACC-9001-1204", "9200", "1204", "Tiger Tower A", "AED", asOf, Stale, "Consistent", [],
                new CollectionsBalanceDto(44_000m, 14_000m, 0m, 30_000m, 500m, 14_500m, 10_000m, new CollectionsNextPaymentDto("INS-4", Today.AddDays(5), 10_000m)),
                new CollectionsReminderEligibilityDto(true, 24_000m, null, []),
                [
                    new CollectionsInstalmentDto("INS-1", 1, Today.AddMonths(-3), 10_000m, 10_000m, 0m, "Paid"),
                    new CollectionsInstalmentDto("INS-2", 2, Today.AddMonths(-2), 10_000m, 6_000m, 4_000m, "Overdue"),
                    new CollectionsInstalmentDto("INS-4", 4, Today.AddDays(5), 10_000m, 2_000m, 8_000m, "PartiallyPaid"),
                ],
                [new CollectionsChargeDto("FINE-1", "Fine", "Late payment fine", 500m, 500m, null, true)]);
            var settled = new CollectionsAccountDto(
                "ACC-9001-0805", "9201", "0805", "Tiger Marina Residences", "AED", asOf, Stale, "Consistent", [],
                new CollectionsBalanceDto(0m, 0m, 0m, 0m, 0m, 0m, 0m, null),
                new CollectionsReminderEligibilityDto(false, 0m, "Settled", []), [], []);
            return new CollectionsOutstandingResponseDto("9001", "Test source", asOf, Stale, Today, [arrears, settled],
                new CollectionsViewerDto(CanSend, ["VoiceBot", "Email"]),
                new CollectionsDocumentsDto(false, false, "No verified receipt or statement-of-account document API is connected."));
        }

        private static CollectionsReminderListResultDto Reminders() => new(
        [
            new CollectionsReminderDto(12, "9001", "ACC-9001-1204", "9200", "OverdueMoreThanOneMonth", "VoiceBot", "2026-10", "Delivered", null,
                14_000m, "AED", false, Now.AddDays(-1), null, "Integration", Now.AddDays(-1), Now.AddDays(-1), Now.AddDays(-1), null, null,
                [
                    new CollectionsReminderEventDto(1, "tigercs:queued", "Queued", Now.AddDays(-1), Now.AddDays(-1), null, null, null, null, null, false, false, null, null, null),
                    new CollectionsReminderEventDto(2, "resp-1", "CustomerResponded", Now.AddDays(-1), Now.AddDays(-1), null, "AlreadyPaid", "conv-1", null, null, true, true, "Linked", 77, "TG-COL-00077"),
                ])
        ], 1, 1, 20);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            var path = Uri.UnescapeDataString(request.RequestUri.AbsolutePath);

            if (path.StartsWith("/api/genesys/collections", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Post)
                {
                    LastPostBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                    var reminder = Reminders().Items[0] with { ReminderId = 13, Channel = "Email", ReminderType = "Manual", Status = "Queued", Amount = 14_000m };
                    return Json(HttpStatusCode.Created, new CreateCollectionsReminderResponseDto("Created", reminder));
                }

                if (path.EndsWith("/reminders", StringComparison.Ordinal))
                {
                    return Mode is "forbidden" ? Problem(HttpStatusCode.Forbidden, "collections-forbidden") : Json(HttpStatusCode.OK, Reminders());
                }

                return Mode switch
                {
                    "forbidden" => Problem(HttpStatusCode.Forbidden, "collections-forbidden"),
                    "disabled" => Problem(HttpStatusCode.ServiceUnavailable, "collections-disabled"),
                    "unavailable" => Problem(HttpStatusCode.ServiceUnavailable, "collections-source-unavailable"),
                    "notfound" => Problem(HttpStatusCode.NotFound, "collections-customer-not-found"),
                    _ when path.EndsWith("/outstanding", StringComparison.Ordinal) => Json(HttpStatusCode.OK, Outstanding()),
                    _ => Json(HttpStatusCode.OK, new CollectionsPaymentsResponseDto("9001", "Test source", Now, false,
                    [
                        new CollectionsPaymentDto("PAY-3", "ACC-9001-1204", "9200", "1204", Today.AddDays(-1), null, 6_000m, "AED", "BankTransfer", "Customer upload", "PendingVerification", false, false),
                        new CollectionsPaymentDto("PAY-2", "ACC-9001-1204", "9200", "1204", Today.AddMonths(-2), Today.AddMonths(-2), 6_000m, "AED", "Cheque", "CHQ-4411", "Posted", true, false),
                    ], 2, 1, 20)),
                };
            }

            object? body = path switch
            {
                "/api/users/me" => new CurrentUserResponseDto(ViewerId, "Test Supervisor", [Roles.CsSupervisor], [new DepartmentMembershipDto(2, "Customer Service", true)], true),
                "/api/departments" => new[] { new DepartmentDto(2, "Customer Service") },
                "/api/customers/profile/crm:9001" => Profile,
                "/api/customers/profile/phone:+971501112222" => Caller,
                _ => null,
            };

            return body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(HttpStatusCode.OK, body);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = JsonContent.Create(body, body.GetType()) };

        private static HttpResponseMessage Problem(HttpStatusCode status, string type) =>
            new(status) { Content = JsonContent.Create(new { type = $"https://tigercs.internal/problems/{type}", title = type, detail = type }) };
    }
}

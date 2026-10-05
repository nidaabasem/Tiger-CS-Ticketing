// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
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
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Web;

/// <summary>
/// The Customer Profile's Payment tab through the real Razor pipeline against
/// a fake TigerCS.Api: every state the specification names (loading; no
/// linked account; account selection required; current account with zero
/// balance; unavailable with Retry; stale with timestamp; forbidden), the
/// lazily fetched panel, and Send Reminder from a candidate.
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

    private bool CollectionsCalled => _api.Requests.Any(r => r.Contains("/api/collections", StringComparison.Ordinal));

    [Fact]
    public async Task ThePaymentTab_IsDeferredWithALoadingState_AndTheProfileCallsNoFinancialRoute()
    {
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001"));

        Assert.Contains("<label class=\"tab-label\" for=\"tab-payment\">Payment</label>", html, StringComparison.Ordinal);
        Assert.Contains("data-payment-src=\"/Customers/crm%3A9001?handler=PaymentPanel\"", html, StringComparison.Ordinal);
        Assert.Contains("data-payment-state=\"Deferred\"", html, StringComparison.Ordinal);
        Assert.Contains("Loading payment details…", html, StringComparison.Ordinal);
        Assert.False(CollectionsCalled);
        Assert.DoesNotContain("/api/genesys", string.Join("\n", _api.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeveralAccounts_RequireASelection_BeforeAnyDetail()
    {
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment"));

        Assert.Contains("data-payment-state=\"SelectAccount\"", html, StringComparison.Ordinal);
        Assert.Contains("This customer has 2 finance accounts.", html, StringComparison.Ordinal);
        Assert.Contains("data-payment-account-link=\"ACC-45001\"", html, StringComparison.Ordinal);
        Assert.Contains("AED 30,500.00 <small>due now</small>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Instalments", html, StringComparison.Ordinal);
        Assert.DoesNotContain("view=instalments", string.Join("\n", _api.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASelectedAccount_ShowsTheSourceFigures_InstalmentsHistoryAndReminders()
    {
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment&account=ACC-45001"));

        Assert.Matches(new Regex("id=\"tab-payment\"[^>]*checked"), html);
        Assert.Contains("data-payment-state=\"Loaded\"", html, StringComparison.Ordinal);
        Assert.Contains("AED 30,500.00", html, StringComparison.Ordinal);       // amount due now
        Assert.Contains("AED 45,000.00", html, StringComparison.Ordinal);       // remaining principal
        Assert.Contains("Payable penalties", html, StringComparison.Ordinal);
        Assert.Contains("overlaps the overdue, due-today and future amounts", html, StringComparison.Ordinal);
        Assert.Contains("Last updated", html, StringComparison.Ordinal);
        Assert.Contains("Currency <strong>AED</strong>", html, StringComparison.Ordinal);

        Assert.Contains(">Partially paid<", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"badge badge-pay-critical\">Overdue</span>", html, StringComparison.Ordinal); // partially paid AND overdue
        Assert.Contains("RCT-70001", html, StringComparison.Ordinal);
        Assert.Contains("INST-MAY-2026", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Tickets/77\">TG-COL-00077</a>", html, StringComparison.Ordinal);
        Assert.Contains("data-documents-unavailable", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Download", html, StringComparison.Ordinal);

        var requests = string.Join("\n", _api.Requests);
        Assert.Contains("/api/collections/customers/9001/payments?accountId=ACC-45001&view=instalments", requests, StringComparison.Ordinal);
        Assert.Contains("/api/collections/customers/9001/payments?accountId=ACC-45001&view=history", requests, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendReminder_ShowsTheCandidatesAmountAccountAndChannels_ForAPermittedViewer()
    {
        _api.CanSend = true;
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment&account=ACC-45001"));

        Assert.Contains(">Send Reminder</button>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>AED 30,000.00</strong>", html, StringComparison.Ordinal);
        Assert.Contains("unpaid principal older than one calendar month", html, StringComparison.Ordinal);
        Assert.Contains("name=\"channels\" value=\"Email\" checked", html, StringComparison.Ordinal);
        Assert.Contains("name=\"channels\" value=\"Sms\" checked", html, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"VoiceBot\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"idempotencyKey\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendReminder_IsHidden_WithoutTheGrant_OnStaleData_AndWhenNothingIsDue()
    {
        _api.CanSend = false;
        Assert.DoesNotContain("Send Reminder", await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment&account=ACC-45001")), StringComparison.Ordinal);

        _api.CanSend = true;
        _api.Stale = true;
        var stale = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment&account=ACC-45001"));
        Assert.DoesNotContain(">Send Reminder</button>", stale, StringComparison.Ordinal);
        Assert.Contains("data-balance-stale", stale, StringComparison.Ordinal);
        Assert.Contains("Reminders cannot be sent on stale figures.", stale, StringComparison.Ordinal);

        _api.Stale = false;
        var settled = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment&account=ACC-45002"));
        Assert.Contains("data-balance-settled", settled, StringComparison.Ordinal);
        Assert.Contains("data-no-candidate", settled, StringComparison.Ordinal);
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
    public async Task Unavailable_SaysSoWithRetry_NeverZero_AndKeepsReminderHistory()
    {
        _api.Mode = "unavailable";
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment"));

        Assert.Contains("Balance unavailable.", html, StringComparison.Ordinal);
        Assert.Contains("data-payment-retry>Retry</a>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("AED 0.00", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Amount due now", html, StringComparison.Ordinal);
        Assert.Contains("TG-COL-00077", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_NoLinkedAccount_AndAPhoneOnlyCaller_EachSaySo()
    {
        _api.Mode = "disabled";
        Assert.Contains("Collections is not enabled in this environment", await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment")), StringComparison.Ordinal);

        _api.Mode = "notfound";
        Assert.Contains("No finance account is linked to this customer.", await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment")), StringComparison.Ordinal);

        _api.Mode = "loaded";
        _api.Requests.Clear();
        Assert.Contains("Payment information is available only for customers identified in Tiger CRM or PACT.",
            await Ok(await Client().GetAsync("/Customers/phone:%2B971501112222?tab=payment")), StringComparison.Ordinal);
        Assert.False(CollectionsCalled);
    }

    [Fact]
    public async Task ThePanelHandler_ReturnsTheTabAlone_ForTheRequestedAccount()
    {
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?handler=PaymentPanel&account=ACC-45001"));

        Assert.StartsWith("<div class=\"payment-tab\" data-payment-state=\"Loaded\" data-payment-account=\"ACC-45001\"", html.TrimStart(), StringComparison.Ordinal);
        Assert.DoesNotContain("<html", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendReminder_QueuesTheCandidate_WithTheFormsIdempotencyKey_AndReturnsToTheTab()
    {
        _api.CanSend = true;
        var client = Client();
        var page = await Ok(await client.GetAsync("/Customers/crm:9001?tab=payment&account=ACC-45001"));
        string Field(string name) => Regex.Match(page, $"name=\"{name}\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var post = await client.PostAsync("/Customers/crm:9001?handler=SendReminder", new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value),
            new("candidateId", Field("candidateId")),
            new("accountId", "ACC-45001"),
            new("idempotencyKey", Field("idempotencyKey")),
            new("channels", "Email"),
            new("channels", "Sms"),
            new("language", "en"),
        ]));

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal("/Customers/crm%3A9001?tab=payment&account=ACC-45001#payment", post.Headers.Location!.OriginalString);

        var sent = JsonSerializer.Deserialize<QueueCollectionsReminderRequestDto>(_api.LastPostBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("CAND-1", sent.CandidateId);
        Assert.Equal(["Email", "Sms"], sent.Channels);
        Assert.Equal(Field("idempotencyKey"), _api.LastIdempotencyKey);

        var back = await Ok(await client.GetAsync(post.Headers.Location));
        Assert.Contains("Reminder REM-90001 queued for AED 30,000.00 on Email, SMS. Queued does not mean delivered.", back, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChangedCandidate_IsReportedToTheAgent()
    {
        _api.CanSend = true;
        _api.QueueAnswer = HttpStatusCode.Conflict;
        var client = Client();
        var page = await Ok(await client.GetAsync("/Customers/crm:9001?tab=payment&account=ACC-45001"));

        var post = await client.PostAsync("/Customers/crm:9001?handler=SendReminder", new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value),
            new("candidateId", "CAND-1"), new("accountId", "ACC-45001"), new("channels", "Email"),
        ]));
        var back = await Ok(await client.GetAsync(post.Headers.Location));

        Assert.Contains("The amount or eligibility changed since this reminder was offered.", back, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChargesAndCreditsTheSourceDidNotReport_SaySo_WhilePrincipalIsShown()
    {
        _api.ChargesUnreported = true;
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?tab=payment&account=ACC-45001"));

        Assert.Contains("data-payment-state=\"Loaded\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-balance-unavailable", html, StringComparison.Ordinal);
        Assert.Contains("AED 45,000.00", html, StringComparison.Ordinal);                  // principal still shown
        Assert.Contains("<dt>Payable penalties</dt><dd>Not provided by source</dd>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Applied credit</dt><dd>Not provided by source</dd>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Amount due now</dt><dd>Not provided by source</dd>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<dt>Payable fees</dt><dd>AED 0.00</dd>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SiteCssAndJs_AreVersioned_SoADeploymentNeverLeavesAStaleStylesheetHidingThePanel()
    {
        // Root cause of the "empty Payment panel" report: an unversioned, heuristically cached
        // site.css from before the tab existed has no #panel-payment display rule.
        var profile = await Ok(await Client().GetAsync("/Customers/crm:9001"));
        var login = await Ok(await _factory.CreateClient().GetAsync("/Login"));

        foreach (var html in new[] { profile, login })
        {
            Assert.Matches(new Regex("href=\"/css/site\\.css\\?v=[A-Za-z0-9_-]{20,}\""), html);
            Assert.Matches(new Regex("src=\"/js/site\\.js\\?v=[A-Za-z0-9_-]{20,}\""), html);
        }
    }

    [Fact]
    public async Task ACrmCustomerWithNoFinancialSource_GetsTheExplicitNotMappedMessage_NotAnEmptyOrTemporaryState()
    {
        _api.Mode = "nosource";
        var html = await Ok(await Client().GetAsync("/Customers/crm:9001?handler=PaymentPanel"));

        Assert.Contains("data-payment-state=\"NotMapped\"", html, StringComparison.Ordinal);
        Assert.Contains("data-payment-not-mapped", html, StringComparison.Ordinal);
        Assert.Contains("no verified mapping from a CRM customer to a PACT tenant exists", html, StringComparison.Ordinal);
        Assert.DoesNotContain("temporarily unavailable", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-balance-unavailable", html, StringComparison.Ordinal);
        Assert.DoesNotContain("due now", html, StringComparison.Ordinal);
        Assert.Contains("Reminder history", html, StringComparison.Ordinal);   // TigerCS's own record is still shown
        Assert.Contains("/payment-summary", string.Join("\n", _api.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APactCustomer_IsDeferred_NotRefusedAsNonCrm()
    {
        var html = await Ok(await Client().GetAsync("/Customers/ext:Pact:3001"));

        Assert.Contains("data-payment-state=\"Deferred\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-payment-state=\"NotCrmCustomer\"", html, StringComparison.Ordinal);
        Assert.False(CollectionsCalled);
    }

    [Fact]
    public async Task APactCustomer_ShowsEdsmFiguresPerCompanyAndModel_NeverZeroForMissingValues()
    {
        var html = await Ok(await Client().GetAsync("/Customers/ext:Pact:3001?tab=payment"));
        var requests = string.Join("\n", _api.Requests);

        Assert.Contains("data-payment-state=\"EdsmSummary\"", html, StringComparison.Ordinal);
        Assert.Contains("/api/collections/customers/by-key/ext%3APact%3A3001/payment-summary", requests, StringComparison.Ordinal);
        Assert.Contains("data-edsm-company=\"4\" data-edsm-model=\"Owned\"", html, StringComparison.Ordinal);
        Assert.Contains("Tiger Group Dubai · owned (sale)", html, StringComparison.Ordinal);
        Assert.Contains("Hirmas Dubai · rented (lease)", html, StringComparison.Ordinal);
        Assert.Contains("1,250,000.00", html, StringComparison.Ordinal);                 // EDSM's own formatted string
        Assert.Contains("Definition of Total.", html, StringComparison.Ordinal);
        Assert.Matches(new Regex("data-edsm-field=\"paidAmount\" data-edsm-amount-status=\"Missing\">\\s*<dt>Paid.*?</dt>\\s*<dd>\\s*Not provided", RegexOptions.Singleline), html);
        Assert.Contains("None above zero", html, StringComparison.Ordinal);               // owned blank fine
        Assert.Contains("Not computed for rented companies", html, StringComparison.Ordinal);
        Assert.Contains("-500.00", html, StringComparison.Ordinal);                      // rented due may be negative
        Assert.Contains("EDSM sent “1.500,00”", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">0.00", html, StringComparison.Ordinal);
        Assert.Contains("AED <small class=\"field-hint\">(configured in TigerCS — EDSM returns no currency)</small>", html, StringComparison.Ordinal);
        Assert.Contains("EDSM returns no as-of time", html, StringComparison.Ordinal);
        Assert.Contains("up to about 20 minutes", html, StringComparison.Ordinal);
        Assert.Contains("data-mapping-verified=\"Cached\"", html, StringComparison.Ordinal);
        Assert.Contains("(PACT contracts, reused)", html, StringComparison.Ordinal);
        Assert.Contains("data-edsm-transactions=\"Paid\"", html, StringComparison.Ordinal);
        Assert.Contains("Refunds appear in this list as positive payments.", html, StringComparison.Ordinal);
        Assert.Contains("data-edsm-due-installments=\"Available\"", html, StringComparison.Ordinal);
        Assert.Contains("Whether a row is still unpaid is not confirmed.", html, StringComparison.Ordinal);
        Assert.Contains("data-edsm-company=\"7\" data-edsm-model=\"Rented\" data-edsm-status=\"Unauthorized\"", html, StringComparison.Ordinal);
        Assert.Contains("EDSM rejected the configured API key.", html, StringComparison.Ordinal);
        Assert.Contains("data-detail-unavailable", html, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=SendReminder", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/outstanding", requests, StringComparison.Ordinal);
        Assert.DoesNotContain("/reminders", requests, StringComparison.Ordinal);
        Assert.DoesNotContain("/candidates", requests, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APactCustomer_WhenTheSummaryIsUnavailable_ShowsNoFigures()
    {
        _api.Mode = "unavailable";
        var html = await Ok(await Client().GetAsync("/Customers/ext:Pact:3001?tab=payment"));

        Assert.Contains("data-payment-state=\"Unavailable\"", html, StringComparison.Ordinal);
        Assert.Contains("data-balance-unavailable", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-edsm-summary", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Reminder history", html, StringComparison.Ordinal);
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
        public bool ChargesUnreported { get; set; }
        public HttpStatusCode QueueAnswer { get; set; } = HttpStatusCode.Accepted;
        public List<string> Requests { get; } = [];
        public string? LastPostBody { get; private set; }
        public string? LastIdempotencyKey { get; private set; }

        private static readonly DateOnly Today = DateOnly.FromDateTime(Now);

        private static readonly CustomerDirectoryProfileDto Profile = new(
            "crm:9001", "Crm", "Test Buyer", ["+971500000900"], [], "Crm", 9001, null, null, 0, 1, Now.AddDays(-30), Now.AddDays(-1), 6, [], [], []);

        private static readonly CustomerDirectoryProfileDto PactCustomer = new(
            "ext:Pact:3001", "External", "Fatima Noor", ["+971500000002"], [], "Pact", null, "Pact", "3001", 0, 1, Now.AddDays(-30), Now.AddDays(-1), 8, [], [], []);

        private static readonly CollectionsPactContractRefDto Contract = new("88001", "41230", "0304", "Tiger Marina Residences", "Residential");

        private static CollectionsEdsmFieldDto F(string key, string label, string status, decimal? value, string? raw, string? meaning = null) =>
            new(key, label, $"Definition of {label}.", status, value, raw, meaning);

        private static CollectionsPaymentSummaryResponseDto CrmNotMapped() => new(
            "crm:9001", "NotMapped",
            "This customer is identified by Tiger CRM (customerId 9001). EDSM is keyed by PACT companyID and tenantID, and no verified mapping from a CRM customer to a PACT tenant exists.",
            null, "Pact", Now, null, "AED", "Configured", "en-US", 10, 20, null, null, [], []);

        private static CollectionsPaymentSummaryResponseDto Summary() => new(
            "ext:Pact:3001", "Mapped", null, "3001", "Pact", Now, null, "AED", "Configured", "en-US", 10, 20, Now.AddMinutes(-5), "Cached",
            [
                new CollectionsCompanyPaymentSummaryDto(4, "Tiger Group Dubai", "Owned", "Available", null, [Contract],
                [
                    F("paidAmount", "Paid", "Missing", null, null),
                    F("dueAmount", "Due", "Unreadable", null, "1.500,00"),
                    F("outstandingAmount", "Not yet due", "Provided", 437_500m, "437,500.00"),
                    F("lateFines", "Late fines", "Empty", null, "", "ZeroOrLess"),
                    F("totalAmount", "Total", "Provided", 1_250_000m, "1,250,000.00"),
                ], "NotChecked", false, [],
                [
                    new CollectionsEdsmTransactionListDto("Paid", "Available", null, null,
                        [new CollectionsEdsmTransactionDto(500_000m, "Provided", "500,000.00", new DateOnly(2026, 1, 15), "15-Jan-2026", null, null)]),
                ],
                new CollectionsEdsmDueInstallmentsDto("Available", null, new DateOnly(2026, 9, 4), new DateOnly(2026, 11, 5),
                    [new CollectionsEdsmDueInstallmentDto(41230, "PDC-0412", "000412", new DateOnly(2026, 9, 15), 62_500m, "Due ")])),
                new CollectionsCompanyPaymentSummaryDto(25, "Hirmas Dubai", "Rented", "Available", null, [],
                [
                    F("dueAmount", "Due", "Provided", -500m, "-500.00"),
                    F("lateFines", "Late fines", "Empty", null, "", "NotComputedForRented"),
                ], "NotChecked", false, [],
                [new CollectionsEdsmTransactionListDto("Paid", "Available", null, "Refunds appear in this list as positive payments.", [])], null),
                new CollectionsCompanyPaymentSummaryDto(7, "Alsabeel Sharjah", "Rented", "Unauthorized", "EDSM rejected the configured API key.", [], [],
                    "NotChecked", false, [], [], null),
            ],
            []);

        private static readonly CustomerDirectoryProfileDto Caller = new(
            "phone:%2B971501112222", "Phone", null, ["+971501112222"], [], "Unverified", null, null, null, 0, 0, Now.AddDays(-3), Now.AddDays(-3), 7, [], [], []);

        private CollectionsAccountDto Arrears => new(
            "ACC-45001", 45001, "Example Tower", "1205", "AED", Stale ? Now.AddHours(-5) : Now.AddMinutes(-2), Stale ? "Stale" : "Current",
            45_000m, 30_000m, 0m, 15_000m,
            ChargesUnreported ? null : 500m, ChargesUnreported ? null : 0m, ChargesUnreported ? null : 0m, ChargesUnreported ? null : 30_500m,
            10_000m, Today.AddMonths(-4),
            new CollectionsNextPaymentDto("INST-OCT-2026", Today.AddDays(13), 10_000m), []);

        private static CollectionsAccountDto Settled => new(
            "ACC-45002", 45002, "Example Tower", "0805", "AED", Now.AddMinutes(-2), "Current",
            0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, null, null, []);

        private static CollectionsReminderHistoryResponseDto Reminders() => new(9001, null,
        [
            new CollectionsReminderHistoryItemDto("REM-12", "ACC-45001", "OverdueMonthly", "2026-10:OverdueMonthly", "AED", 30_000m,
                "UnpaidPrincipalOlderThanOneCalendarMonth", Now.AddDays(-1), "Integration",
                [new CollectionsChannelStatusDto("VoiceBot", "Answered", Now.AddDays(-1), 1, null)],
                "AlreadyPaid", 77, "TG-COL-00077",
                [new CollectionsReminderResponseDto("EVT-1", "VoiceBot", "AlreadyPaid", Now.AddDays(-1), true, true, "Created", 77, "TG-COL-00077")])
        ], null);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            var path = Uri.UnescapeDataString(request.RequestUri.AbsolutePath);
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);

            if (path.StartsWith("/api/collections", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Post)
                {
                    LastPostBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                    LastIdempotencyKey = request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null;
                    return QueueAnswer == HttpStatusCode.Conflict
                        ? Problem(HttpStatusCode.Conflict, "CandidateChanged")
                        : Json(HttpStatusCode.Accepted, new CollectionsReminderJobDto("REM-90001", 9001, "ACC-45001", "OverdueMonthly", "2026-10:OverdueMonthly",
                            "AED", 30_000m, "UnpaidPrincipalOlderThanOneCalendarMonth", ["INST-JUN-2026"], Now, "Queued",
                            [new("Email", "Queued", Now, 1, null), new("Sms", "Queued", Now, 1, null)]));
                }

                if (path.EndsWith("/reminders", StringComparison.Ordinal))
                {
                    return Mode == "forbidden" ? Problem(HttpStatusCode.Forbidden, "Forbidden") : Json(HttpStatusCode.OK, Reminders());
                }

                if (path.EndsWith("/candidates", StringComparison.Ordinal))
                {
                    if (!CanSend)
                    {
                        return Problem(HttpStatusCode.Forbidden, "Forbidden");
                    }

                    var items = query["reminderType"] == "OverdueMonthly" && query["accountId"] == "ACC-45001" && !Stale
                        ? new List<CollectionsReminderCandidateDto>
                        {
                            new("CAND-1", 9001, "ACC-45001", 45001, "Example Tower", "1205", "AED", 30_000m, "UnpaidPrincipalOlderThanOneCalendarMonth",
                                ["INST-JUN-2026", "INST-JUL-2026"], Today.AddMonths(-4), ["VoiceBot", "Sms", "Email"], Now, Now.AddMinutes(15))
                        }
                        : [];
                    return Json(HttpStatusCode.OK, new CollectionsReminderCandidatesResponseDto(query["reminderType"]!, "2026-10:" + query["reminderType"], Today, "Asia/Dubai", true, items, null));
                }

                return Mode switch
                {
                    "nosource" when path.EndsWith("/payment-summary", StringComparison.Ordinal) => Json(HttpStatusCode.OK, CrmNotMapped()),
                    "nosource" => Problem(HttpStatusCode.ServiceUnavailable, "FinanceUnavailable"),
                    "forbidden" => Problem(HttpStatusCode.Forbidden, "Forbidden"),
                    "disabled" => Problem(HttpStatusCode.ServiceUnavailable, "CollectionsDisabled"),
                    "unavailable" => Problem(HttpStatusCode.ServiceUnavailable, "FinanceUnavailable"),
                    "notfound" => Problem(HttpStatusCode.NotFound, "AccountNotFound"),
                    _ when path.EndsWith("/payment-summary", StringComparison.Ordinal) => Json(HttpStatusCode.OK, Summary()),
                    _ when path.EndsWith("/outstanding", StringComparison.Ordinal) =>
                        Json(HttpStatusCode.OK, new CollectionsOutstandingResponseDto(9001, Today, Now, Stale ? "Stale" : "Current", "Test source", [Arrears, Settled], null)),
                    _ when query["view"] == "history" => Json(HttpStatusCode.OK, new CollectionsPaymentHistoryResponseDto(9001, query["accountId"]!, "AED", Now, "Current", "history",
                        [new CollectionsPaymentDto("PAY-70001", Today.AddMonths(-5), 5_000m, "BankTransfer", "Posted", "RCT-70001", true, [new("INST-MAY-2026", 5_000m, null)])], null)),
                    _ => Json(HttpStatusCode.OK, new CollectionsInstalmentsResponseDto(9001, query["accountId"]!, "AED", Now, "Current", "instalments",
                    [
                        new CollectionsInstalmentDto("INST-JUN-2026", Today.AddMonths(-4), 5_000m, 2_000m, 3_000m, "PartiallyPaid", true),
                        new CollectionsInstalmentDto("INST-OCT-2026", Today.AddDays(13), 10_000m, 0m, 10_000m, "Upcoming", false),
                    ], null)),
                };
            }

            object? body = path switch
            {
                "/api/users/me" => new CurrentUserResponseDto(ViewerId, "Test Supervisor", [Roles.CsSupervisor], [new DepartmentMembershipDto(2, "Customer Service", true)], true),
                "/api/departments" => new[] { new DepartmentDto(2, "Customer Service") },
                "/api/customers/profile/crm:9001" => Profile,
                "/api/customers/profile/ext:Pact:3001" => PactCustomer,
                "/api/customers/profile/phone:+971501112222" => Caller,
                _ => null,
            };

            return body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(HttpStatusCode.OK, body);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = JsonContent.Create(body, body.GetType()) };

        private static HttpResponseMessage Problem(HttpStatusCode status, string code) =>
            new(status) { Content = JsonContent.Create(new { type = $"https://tigercs.internal/problems/collections/{code}", title = code, detail = code, code, message = code }) };
    }
}

// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Web;

/// <summary>
/// The remaining application screens rendered end to end through the real
/// Razor pipeline — Login (anonymous), Dashboard, the New Ticket wizard's
/// first step and the Administration landing page — proving each renders
/// on the shared shell with the same primitives (page header, panel,
/// filter bar, stepper, empty/error states) and that the Dashboard's
/// shortcuts land on the right Tickets views.
/// </summary>
public sealed class ShellRenderTests : IDisposable
{
    private static readonly Guid ViewerId = Guid.NewGuid();
    private readonly WebApplicationFactory<TigerCsWeb::Program> _factory;

    public ShellRenderTests()
    {
        _factory = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new FakeApi()));
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

    private HttpClient Client(string role = Roles.CsAgent)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        return client;
    }

    private static async Task<string> Ok(HttpResponseMessage response)
    {
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.RequestMessage?.RequestUri}: {(int)response.StatusCode}\n{html[..Math.Min(html.Length, 2000)]}");
        return html;
    }

    [Fact]
    public async Task Login_RendersTheSplitLayout_WithBrandingAndTheSignInForm()
    {
        var html = await Ok(await Client().GetAsync("/Login"));

        Assert.Contains("class=\"login-shell\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"login-aside\"", html, StringComparison.Ordinal);
        Assert.Contains("Tiger <span>Ticketing</span>", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"login-brand__product\">Sign in</div>", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.Identifier\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.Password\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"btn btn-gold btn-block\">Sign In</button>", html, StringComparison.Ordinal);
        // No dead links, no admin-template chrome.
        Assert.DoesNotContain("href=\"#\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("login-corner", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccessDenied_UsesTheSameLayout()
    {
        var html = await Ok(await Client().GetAsync("/AccessDenied"));

        Assert.Contains("class=\"login-aside\"", html, StringComparison.Ordinal);
        Assert.Contains("Access denied", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Tickets\">Back to Tickets</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_RendersSearchAndWorkspaceShortcuts_ThatLandOnEachTicketsView()
    {
        var html = await Ok(await Client().GetAsync("/Dashboard"));

        Assert.Contains("<h1 class=\"page-title\">Dashboard</h1>", html, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\">Dashboard</a>", html, StringComparison.Ordinal);
        Assert.Contains("action=\"/Customers\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Customers/Lookup\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/Tickets\">Open Tickets workspace</a>", html, StringComparison.Ordinal);

        foreach (var (href, label) in new[] { ("/Tickets", "Queue"), ("/Tickets?view=pending", "Pending Interactions"), ("/Tickets?view=my", "My Tickets"), ("/Tickets?view=closed", "Closed") })
        {
            Assert.Contains($"<a class=\"dash-shortcut\" href=\"{href}\">", html, StringComparison.Ordinal);
            Assert.Contains($"<span class=\"dash-shortcut__title\">{label}</span>", html, StringComparison.Ordinal);
        }

        // The Api is unreachable in this fake, so the KPI area shows the shared error state — never a broken layout.
        Assert.Contains("could not be loaded right now", html, StringComparison.Ordinal);
        Assert.Contains("class=\"error-state\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_WithData_RendersKpiCards_FilterDisclosure_AttentionTable_AndBreakdowns()
    {
        // Tests in one class run sequentially, so a static switch on the fake is safe here.
        var client = Client();
        client.DefaultRequestHeaders.Add("X-Test-Populated", "true");
        FakeApi.Populated = true;
        string html;
        try { html = await Ok(await client.GetAsync("/Dashboard")); }
        finally { FakeApi.Populated = false; }

        Assert.Equal(6, Count(html, "class=\"kpi-card kpi-card--icon"));
        Assert.Contains("class=\"filter-disclosure\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"dashboardFilters\"", html, StringComparison.Ordinal);
        Assert.Contains("Tickets Requiring Attention", html, StringComparison.Ordinal);
        Assert.Contains("data-href=\"/Tickets/5\"", html, StringComparison.Ordinal);
        Assert.Contains("Volume by Department", html, StringComparison.Ordinal);
        Assert.Equal(6, Count(html, "class=\"panel dash-card\""));
        // KPI drill-downs land on the Tickets workspace; My Tickets on its own tab.
        Assert.Contains("href=\"/Tickets?", html, StringComparison.Ordinal);
        Assert.Contains("view=my", html, StringComparison.Ordinal);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    [Fact]
    public async Task NewTicket_StepOne_RendersTheStepperAndTheSearchForm()
    {
        var html = await Ok(await Client().GetAsync("/NewTicket"));

        Assert.Contains("<h1 class=\"page-title\">New Ticket</h1>", html, StringComparison.Ordinal);
        Assert.Contains("class=\"wizard-stepper\"", html, StringComparison.Ordinal);
        Assert.Contains("wizard-stepper__item is-current", html, StringComparison.Ordinal);
        foreach (var step in new[] { "Customer", "Property", "Issue", "Review" })
        {
            Assert.Contains($"<span class=\"wizard-stepper__label\">{step}<small>", html, StringComparison.Ordinal);
        }

        Assert.Contains("Step 1 of 4", html, StringComparison.Ordinal);
        Assert.Contains("<h2 class=\"section-card__heading\">Customer</h2>", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Intake.PhoneNumber\"", html, StringComparison.Ordinal);
        Assert.Contains("WhatsApp", html, StringComparison.Ordinal);
        Assert.Contains("Ticket summary", html, StringComparison.Ordinal);
        Assert.Contains("Not selected yet", html, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\">Tickets</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Administration_Landing_RendersOnTheSharedShell_WithTheSubNavAndAreaCards()
    {
        var html = await Ok(await Client(Roles.SystemAdministrator).GetAsync("/Admin"));

        Assert.Contains("aria-current=\"page\">Administration</a>", html, StringComparison.Ordinal);
        Assert.Contains("class=\"admin-subnav\"", html, StringComparison.Ordinal);
        Assert.Contains("<h1 class=\"page-title\">Administration</h1>", html, StringComparison.Ordinal);
        Assert.Contains("class=\"admin-card\"", html, StringComparison.Ordinal);

        var users = await Ok(await Client(Roles.SystemAdministrator).GetAsync("/Admin/Users"));
        Assert.Contains("<nav class=\"breadcrumb\" aria-label=\"Breadcrumb\">", users, StringComparison.Ordinal);
        Assert.Contains("<span class=\"sep\">/</span>", users, StringComparison.Ordinal);
        Assert.Contains("class=\"filter-bar\"", users, StringComparison.Ordinal);
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers.TryGetValue("X-Test-Role", out var header) ? header.ToString() : Roles.CsAgent;
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, ViewerId.ToString()),
                new Claim(ClaimTypes.Name, "Test Agent"),
                new Claim(ClaimTypes.Role, role),
            ], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    /// <summary>The channel catalogue always answers; the dashboard overview answers only for a page request that carried X-Test-Populated (the test flips the fake's switch); everything else is unreachable, so each page's error/empty states are exercised.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        private static readonly DateTime Now = new(2026, 9, 12, 9, 0, 0, DateTimeKind.Utc);
        public static volatile bool Populated;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object? body = path switch
            {
                "/api/channels" => new[] { new ChannelDto(3, "WhatsApp", "WHATSAPP", true, true, true, 2) },
                "/api/dashboard/overview" when Populated => Overview(),
                _ => null,
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) });
        }

        private static DashboardOverviewDto Overview()
        {
            var filters = new DashboardAppliedFiltersDto(new DateOnly(2026, 8, 13), new DateOnly(2026, 9, 12), null, null, null, null, null, null);
            DashboardBreakdownItemDto Item(string key, string label, int count, double pct) => new(key, label, count, pct);
            return new DashboardOverviewDto(
                filters,
                new DashboardKpisDto(42, 6, 11, 3, 4, 2),
                120,
                [Item("3", "WhatsApp", 60, 50), Item("1", "Phone", 45, 37.5), Item("4", "Social Media", 15, 12.5)],
                [Item("9", "Payment Reminder", 70, 58.3), Item("10", "Complaint", 50, 41.7)],
                [Item("2", "Customer Service", 80, 66.7), Item("3", "Collections", 40, 33.3)],
                [Item("Open", "Open", 30, 25), Item("InProgress", "In Progress", 50, 41.7), Item("Resolved", "Resolved", 40, 33.3)],
                [Item("1", "Critical", 5, 4.2), Item("2", "High", 25, 20.8), Item("3", "Medium", 60, 50), Item("4", "Low", 30, 25)],
                [Item("UnderOneDay", "< 1 day", 10, 23.8), Item("OneToThreeDays", "1–3 days", 20, 47.6), Item("OverSevenDays", "> 7 days", 12, 28.6)],
                [
                    new DashboardRecentTicketDto(5, "TG-CS-00005", "Mariam Al Falasi", "T-1204", "Tiger Tower", 2, "Customer Service", 9, "Payment Reminder", 1, "Open", "Breached", Now.AddHours(-5), null, null, Now.AddDays(-2)),
                    new DashboardRecentTicketDto(6, "TG-CS-00006", "Omar Haddad", "P-08", "Palm Residence", 3, "Collections", 10, "Complaint", 2, "InProgress", "Running", Now.AddHours(3), ViewerId, "Test Agent", Now.AddHours(-20)),
                ],
                new DashboardFilterOptionsDto(
                    [new DashboardFilterOptionDto("2", "Customer Service"), new DashboardFilterOptionDto("3", "Collections")],
                    [new DashboardFilterOptionDto(ViewerId.ToString(), "Test Agent")],
                    [new DashboardFilterOptionDto("3", "WhatsApp"), new DashboardFilterOptionDto("1", "Phone")],
                    [new DashboardFilterOptionDto("9", "Payment Reminder", 2), new DashboardFilterOptionDto("10", "Complaint", 3)],
                    [new DashboardFilterOptionDto("Open", "Open"), new DashboardFilterOptionDto("InProgress", "In Progress")],
                    [new DashboardFilterOptionDto("1", "Critical"), new DashboardFilterOptionDto("2", "High")]));
        }
    }
}

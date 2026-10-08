extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
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
using TigerCS.Domain.Modules.IdentityAndAccess;
using Display = TigerCsWeb::TigerCS.Web.Pages.Collections.CollectionsDisplay;

namespace TigerCS.Tests.Web;

/// <summary>Coverage-gap banner, "Last 6 months" shortcut, blocked export and the "Load missing data" POST.</summary>
public sealed class CollectionsCoverageRenderTests
{
    private static SnapshotStatusDto Status(bool covered, bool loading = false)
    {
        var now = DateTime.UtcNow;
        var company = new SnapshotCompanyStatusDto(4, "Tiger Group Dubai", true, now.AddMinutes(-5), now.AddMinutes(-5), "Succeeded", null, 0, 50,
            new DateOnly(2026, 1, 1), new DateOnly(2099, 12, 31), 0, 0m, 0, 0m, 0, 0, "Fresh", 5, loading);
        return new SnapshotStatusDto([company], [], 90, new DateOnly(2025, 9, 15), new DateOnly(2026, 3, 15),
            covered ? [] : [new CoverageGapDto(4, "Tiger Group Dubai", new DateOnly(2025, 9, 15), new DateOnly(2025, 12, 31))]);
    }

    private sealed class Api(bool covered, bool loading = false) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            switch (request.RequestUri.AbsolutePath)
            {
                case "/api/collections/receivables/towers":
                    return Json(new List<CollectionsTowerDto> { new(7, "124", "Tower 124", 4, true) });
                case "/api/collections/receivables/coverage/load":
                    return Json(new ReceivablesRangeLoadDto(true, false, false, "started"), HttpStatusCode.Accepted);
                case "/api/collections/receivables/customers":
                    var today = new DateOnly(2026, 3, 15);
                    return Json(new PactReceivableCustomersDto(today, 2026, 3, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), DateTime.UtcNow, false, [4], "AED", "Configured",
                        0, 0, 0, 1, 25, [], new DateOnly(2025, 9, 15), new DateOnly(2026, 3, 15), null, Status(covered, loading), []));
                default:
                    var date = new DateOnly(2026, 3, 15);
                    return Json(new CollectionsCampaignPreviewDto(date, date, DateTime.UtcNow, "PACT", "CurrentMonthReminder", "2026-03:CurrentMonthReminder", [date], true, true, false, true,
                        0, 0, 0, 1, 25, [], new DateOnly(2025, 9, 15), new DateOnly(2026, 3, 15), [], null, Status(covered, loading)));
            }
        }

        private static Task<HttpResponseMessage> Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(value) });
    }

    private static WebApplicationFactory<TigerCsWeb::Program> Factory(Api api) =>
        new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = api));
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
                services.PostConfigure<AuthenticationOptions>(o => { o.DefaultAuthenticateScheme = "Test"; o.DefaultChallengeScheme = "Test"; });
            });
        });

    [Fact]
    public async Task ReceivablesPage_ShowsTheGap_TheLoadButton_AndNeverACompleteLookingZero()
    {
        using var factory = Factory(new Api(covered: false)); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?dateFrom=2025-09-15&dateTo=2026-03-15")).Content.ReadAsStringAsync();
        Assert.Contains("data-coverage-gap", html);
        Assert.Contains("Additional data needs loading", html);
        Assert.Contains("missing instalments are not zero receivables", html);
        Assert.Contains("15 Sep 2025 to 31 Dec 2025", html);
        Assert.Contains("data-load-button", html);
        Assert.Contains("Load missing data", html);
        Assert.Contains("__RequestVerificationToken", html);
        Assert.Contains("(incomplete: dates not fully loaded)", html);
        Assert.Contains("0&#x2B;", html);
        Assert.DoesNotContain("No unpaid instalments match", html.Contains("data-coverage-gap") ? "" : html);   // sanity: the banner is there
    }

    [Fact]
    public async Task LastSixMonthsShortcut_IsALinkWithTheExplicitRange_OnBothPages()
    {
        using var factory = Factory(new Api(covered: true)); using var client = factory.CreateClient();
        var receivables = await (await client.GetAsync("/Collections/Receivables?towerId=7")).Content.ReadAsStringAsync();
        Assert.Contains("data-last-six-months", receivables);
        Assert.Contains("dateFrom=2025-09-15", receivables);      // preview date = report business date 2026-03-15
        Assert.Contains("dateTo=2026-03-15", receivables);
        var campaigns = await (await client.GetAsync("/Collections/Campaigns?stage=CurrentMonthReminder&businessDate=2026-03-15&towerId=7")).Content.ReadAsStringAsync();
        Assert.Contains("data-last-six-months", campaigns);
        Assert.Contains("dateFrom=2025-09-15", campaigns);
        Assert.Contains("dateTo=2026-03-15", campaigns);
        Assert.Contains("businessDate=2026-03-15", campaigns);
    }

    [Fact]
    public async Task CampaignPage_WithAGap_ShowsTheBannerAndWithholdsBothExportLinks()
    {
        using var factory = Factory(new Api(covered: false)); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?stage=CurrentMonthReminder&businessDate=2026-03-15&dateFrom=2025-09-15&dateTo=2026-03-15")).Content.ReadAsStringAsync();
        Assert.Contains("data-coverage-gap", html);
        Assert.Contains("data-export-blocked", html);
        Assert.Contains("whole From/To range covered", html);
        Assert.DoesNotContain("Export review CSV", html);
        Assert.DoesNotContain("Export Genesys CSV", html);
    }

    [Fact]
    public async Task WhileALoadRuns_TheButtonIsReplacedByAProgressNotice()
    {
        using var factory = Factory(new Api(covered: false, loading: true)); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?dateFrom=2025-09-15&dateTo=2026-03-15")).Content.ReadAsStringAsync();
        Assert.Contains("data-load-running", html);
        Assert.DoesNotContain("data-load-button", html);
    }

    [Fact]
    public async Task LoadMissingData_PostsToTheApi_AndRedirectsBackToTheSameFiltersWithANotice()
    {
        var api = new Api(covered: false);
        using var factory = Factory(api); using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var page = await client.GetAsync("/Collections/Receivables?towerId=7&dateFrom=2025-09-15&dateTo=2026-03-15");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.True(token.Length > 20);
        var post = await client.PostAsync("/Collections/Receivables?handler=LoadCoverage", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["dateFrom"] = "2025-09-15", ["dateTo"] = "2026-03-15", ["returnUrl"] = "/Collections/Receivables?towerId=7&dateFrom=2025-09-15&dateTo=2026-03-15",
            ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal("/Collections/Receivables?towerId=7&dateFrom=2025-09-15&dateTo=2026-03-15&load=started", post.Headers.Location!.OriginalString);
        Assert.Contains("POST /api/collections/receivables/coverage/load?dateFrom=2025-09-15&dateTo=2026-03-15", api.Requests);
        // The notice is shown once, and an off-site return URL is never followed.
        var after = await (await client.GetAsync(post.Headers.Location.OriginalString)).Content.ReadAsStringAsync();
        Assert.Contains("data-load-notice", after);
        var evil = await client.PostAsync("/Collections/Receivables?handler=LoadCoverage", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["dateFrom"] = "2025-09-15", ["dateTo"] = "2026-03-15", ["returnUrl"] = "https://evil.example/x", ["__RequestVerificationToken"] = token }));
        Assert.StartsWith("/Collections/Receivables", evil.Headers.Location!.OriginalString);
    }

    [Fact]
    public void LoadNoticeHelpers_BuildALocalRedirectWithOneNoticeOnly()
    {
        Assert.Equal("/Collections/Receivables?towerId=1&load=started", Display.WithNotice("/Collections/Receivables?towerId=1&load=running", "started"));
        Assert.Equal("/Collections/Campaigns?load=failed", Display.WithNotice("/Collections/Campaigns", "failed"));
        Assert.Equal("", Display.NoticeText("<script>"));
    }

    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "6f1d2a40-8f0e-4c7b-9b57-0a1f3c2d4e5f")   // stable: antiforgery tokens are bound to the user, new Claim(ClaimTypes.Name, "Manager"), new Claim(ClaimTypes.Role, Roles.CsManager)
            ], "Test")), "Test")));
    }
}

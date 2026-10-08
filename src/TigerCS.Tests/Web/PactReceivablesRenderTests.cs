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
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Web;

public sealed class PactReceivablesRenderTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task PageRendersCustomersOrAnExplicitError_WithoutClaimingAnEmptyList(HttpStatusCode apiStatus)
    {
        using var factory = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new Source(apiStatus)));
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                });
            });
        });
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/Collections/Receivables?towerId=7&dateFrom=2026-01-01&dateTo=2026-10-31&search=3001&status=overdue");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("aria-current=\"page\">Collections</a>", html);
        Assert.Contains("/js/receivables.js", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("No customers in this page", html);
        if (apiStatus == HttpStatusCode.OK)
        {
            Assert.Contains("PACT-only customer", html);
            Assert.Contains("Customer ID 3001", html);
            Assert.Contains("Review needed", html);
            Assert.Contains("INV-1", html);
            Assert.Contains("Several source rows share a due date", html);
            // Tower + dates only: no company selector; a searchable tower picker showing number and name; both dates editable.
            Assert.DoesNotContain("name=\"companyId\"", html);
            Assert.Contains("data-tower-picker", html);
            Assert.Contains("<option value=\"7\" selected=\"selected\">124 - Tower 124</option>", html);
            Assert.Contains("<option value=\"\">All towers</option>", html);
            Assert.Contains("name=\"dateFrom\"", html);
            Assert.Contains("value=\"2026-01-01\"", html);
            Assert.Contains("value=\"2026-10-31\"", html);
            Assert.Contains("/js/collections-towers.js", html);
            // Freshness: last success, a failed company, coverage and an unmatched tower are all shown, with a warning.
            Assert.Contains("data-snapshot-warning", html);
            Assert.Contains("Failed 3 times", html);
            Assert.Contains("showing the previous successful data", html);
            Assert.Contains("Tower 119", html);
            Assert.Contains("not in the tower list", html);
            Assert.Contains("Campaign export is disabled until a refresh succeeds", html);
        }
        else
        {
            Assert.DoesNotContain("receivables-table\"", html);
            Assert.Contains(apiStatus == HttpStatusCode.Forbidden
                ? "does not have permission" : "source is unavailable", html);
        }
    }

    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Name, "Test Agent"), new Claim(ClaimTypes.Role, Roles.CsAgent)
            ], "Test")), "Test")));
    }

    private sealed class Source(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/collections/receivables/towers")
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = status == HttpStatusCode.OK ? JsonContent.Create(new List<CollectionsTowerDto> { new(7, "124", "Tower 124", 4, true), new(8, "127", "Faradis", 32, true) }) : null
                });
            if (request.RequestUri!.AbsolutePath != "/api/collections/receivables/customers")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            Assert.Contains("towerId=7", request.RequestUri.Query);
            Assert.DoesNotContain("companyId", request.RequestUri.Query);
            Assert.Contains("dateFrom=2026-01-01", request.RequestUri.Query);
            Assert.Contains("dateTo=2026-10-31", request.RequestUri.Query);
            Assert.Contains("status=overdue", request.RequestUri.Query);
            var now = DateTime.UtcNow;
            var snapshot = new SnapshotStatusDto([
                new SnapshotCompanyStatusDto(4, "Tiger Group Dubai", true, now.AddMinutes(-5), now.AddMinutes(-5), "Succeeded", null, 0, 120, new DateOnly(2000, 1, 1), new DateOnly(2099, 12, 31), 0, 0m, 0, 0m, 0, 0, "Fresh", 5),
                new SnapshotCompanyStatusDto(32, "Tiger Group Sharjah", true, now.AddHours(-5), now.AddMinutes(-2), "Failed", 7416, 3, 80, new DateOnly(2000, 1, 1), new DateOnly(2099, 12, 31), 2, 140m, 0, 0m, 0, 0, "Stale", 300)],
                [new UnmatchedTowerDto(4, "119", "NoMatchingTower", 3, 1234.5m)], 90);
            var today = new DateOnly(2026, 10, 7);
            var report = new PactReceivableCustomersDto(today, 2026, 10, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), DateTime.UtcNow, true, [4, 32], "AED", "Configured",
                1, 0, 1, 1, 25, [new PactReceivableCustomerDto(4, "Tiger Group Dubai", "3001", "PACT-only customer",
                    "971500003001", "", 1, "TP140-101", "", false, true, 0, null, null, "NeedsReview", today.AddDays(-1), 1,
                    [new PactReceivableInstalmentDto(1, "TP140-101", "", "INV-1", "", today.AddDays(-1), 100, "Due", "Unknown", "Overdue", "Installment")])], new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 31), 7, snapshot);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = status == HttpStatusCode.OK ? JsonContent.Create(report) : null
            });
        }
    }
}

extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
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

public sealed class CollectionsCampaignRenderTests
{
    private static WebApplicationFactory<TigerCsWeb::Program> Factory(Source source) =>
        new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = source));
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
                services.PostConfigure<AuthenticationOptions>(options =>
                { options.DefaultAuthenticateScheme = "Test"; options.DefaultChallengeScheme = "Test"; });
            });
        });

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task PageShowsReviewStateOrExplicitError_AndPreservesFilters(HttpStatusCode status)
    {
        using var factory = Factory(new Source(status)); using var client = factory.CreateClient();
        var response = await client.GetAsync("/Collections/Campaigns?stage=CurrentMonthReminder&towerId=7&businessDate=2026-10-14&search=3001");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Collections Campaigns", html);
        Assert.Contains("receivables-loading", html);
        if (status == HttpStatusCode.OK)
        {
            Assert.Contains("Campaign Customer", html); Assert.Contains("Export review CSV", html);
            Assert.DoesNotContain("Export Genesys CSV", html);
            Assert.Contains("Financial source reconciliation required", html);
            Assert.Contains("businessDate=2026-10-14", html); Assert.Contains("towerId=7", html);
            Assert.DoesNotContain("name=\"companyId\"", html);
            Assert.Contains("<option value=\"7\" selected=\"selected\">124 - Tower 124</option>", html);
            Assert.Contains("name=\"dateFrom\"", html); Assert.Contains("value=\"2026-01-01\"", html); Assert.Contains("value=\"2026-10-31\"", html);
            Assert.Contains("/js/collections-campaign-dates.js", html);
            Assert.Contains("Export review CSV", html);   // fresh snapshot: export offered
        }
        else Assert.Contains(status == HttpStatusCode.Forbidden ? "does not have permission" : "source is unavailable", html);
    }

    [Fact]
    public async Task DownloadIsUtf8Csv_WithNoStore_AndInvalidDateNeverCallsApi()
    {
        var source = new Source(HttpStatusCode.OK); using var factory = Factory(source); using var client = factory.CreateClient();
        var download = await client.GetAsync("/Collections/Campaigns?handler=Export&stage=CurrentMonthReminder&mode=review&businessDate=2026-10-14&towerId=7&search=3001");
        Assert.Equal("text/csv", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.True(download.Headers.CacheControl!.NoStore);
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes.Take(3));
        Assert.Contains("CustomerName", Encoding.UTF8.GetString(bytes));
        var reads = source.Reads;
        var invalid = await client.GetAsync("/Collections/Campaigns?businessDate=invalid");
        Assert.Contains("Choose valid filters", await invalid.Content.ReadAsStringAsync()); Assert.Equal(reads, source.Reads);
    }

    [Fact]
    public async Task StaleSnapshot_ShowsTheWarningAndWithholdsBothExportLinks()
    {
        using var factory = Factory(new Source(HttpStatusCode.OK, stale: true)); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?stage=CurrentMonthReminder&towerId=7&businessDate=2026-10-14&search=3001")).Content.ReadAsStringAsync();
        Assert.Contains("data-snapshot-warning", html);
        Assert.Contains("data-export-blocked", html);
        Assert.Contains("Export is disabled", html);
        Assert.DoesNotContain("Export review CSV", html);
        Assert.DoesNotContain("Export Genesys CSV", html);
        Assert.Contains("This data is stale", html);
    }

    private sealed class Source(HttpStatusCode status, bool stale = false) : HttpMessageHandler
    {
        public int Reads { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Reads++;
            if (request.RequestUri!.AbsolutePath == "/api/collections/receivables/towers")
                return Task.FromResult(new HttpResponseMessage(status)
                { Content = status == HttpStatusCode.OK ? JsonContent.Create(new List<CollectionsTowerDto> { new(7, "124", "Tower 124", 4, true) }) : null });
            Assert.Contains("stage=CurrentMonthReminder", request.RequestUri!.Query);
            Assert.Contains("towerId=7", request.RequestUri.Query);
            Assert.DoesNotContain("companyId", request.RequestUri.Query);
            Assert.Contains("businessDate=2026-10-14", request.RequestUri.Query);
            if (status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(status));
            if (request.RequestUri.AbsolutePath == "/api/collections/campaigns/export")
                return Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(new CollectionsCampaignExportDto("campaign-review.csv", "CustomerName\r\nExample\r\n", 1)) });
            Assert.Equal("/api/collections/campaigns/preview", request.RequestUri.AbsolutePath);
            var date = new DateOnly(2026, 10, 14);
            var report = new CollectionsCampaignPreviewDto(date, date, DateTime.UtcNow, "PACT", "CurrentMonthReminder", "2026-10:CurrentMonthReminder",
                [date], true, false, false, true, 1, 0, 1, 1, 25, [new("ID", "ext:Pact:3001", 4, "3001", "Campaign Customer", "+971500003001", "", 101,
                    "TP140-101", "TP140", 500m, "AED", date, "CurrentMonthReminder", "2026-10:CurrentMonthReminder", "NeedsReview", "SourceReconciliationRequired")],
                new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 31), null, 7,
                new SnapshotStatusDto([new SnapshotCompanyStatusDto(4, "Tiger Group Dubai", true, DateTime.UtcNow.AddMinutes(stale ? -300 : -5), DateTime.UtcNow.AddMinutes(stale ? -300 : -5),
                    "Succeeded", null, 0, 50, new DateOnly(2000, 1, 1), new DateOnly(2099, 12, 31), 0, 0m, 0, 0m, 0, 0, stale ? "Stale" : "Fresh", stale ? 300 : 5)], [], 90));
            return Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(report) });
        }
    }

    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Name, "Manager"),
                new Claim(ClaimTypes.Role, Roles.CsManager)
            ], "Test")), "Test")));
    }
}

extern alias TigerCsWeb;

using System.Net;
using System.Text;

namespace TigerCS.Tests.Web;

public sealed class CollectionsCampaignRenderTests
{
    [Fact]
    public async Task InitialPage_HasAllFilters_AndDoesNotWaitForTheCampaignData()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?stage=CurrentMonthReminder&towerId=7&businessDate=2026-10-14&search=3001")).Content.ReadAsStringAsync();
        Assert.Empty(api.Calls("/campaigns/preview"));
        Assert.Contains("data-results-loading", html);
        Assert.Contains("<option value=\"7\" selected=\"selected\">124 - Tower 124</option>", html);
        Assert.Contains("name=\"month\"", html); Assert.Contains("name=\"year\"", html);
        Assert.Contains("name=\"dateFrom\"", html); Assert.Contains("name=\"dateTo\"", html);
        Assert.Contains("data-last-six-months", html);
        Assert.Contains("Minimum outstanding amount (AED)", html); Assert.Contains("value=\"100\"", html);
        Assert.DoesNotContain("name=\"companyId\"", html);
        Assert.Contains("/js/collections-campaign-dates.js", html);
        Assert.Contains("fully paid instalments are never reminder candidates", html);
    }

    [Fact]
    public async Task Results_SendEveryFilter_UseTheUnpaidDateLabel_AndOfferExportsFromTheSameFilters()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&towerId=7&businessDate=2026-10-14&dateFrom=2026-10-01&dateTo=2026-10-31&minAmount=250.5&search=3001")).Content.ReadAsStringAsync();
        var call = Assert.Single(api.Calls("/campaigns/preview"));
        foreach (var expected in new[] { "stage=CurrentMonthReminder", "towerId=7", "businessDate=2026-10-14", "dateFrom=2026-10-01", "dateTo=2026-10-31", "minAmount=250.5", "search=3001" }) Assert.Contains(expected, call);
        Assert.DoesNotContain("companyId", call);
        Assert.Contains("Earliest unpaid due date", html);
        Assert.DoesNotContain(">Earliest qualifying due date<", html);
        Assert.Contains("Campaign Customer", html); Assert.Contains("Export review CSV", html);
        Assert.DoesNotContain("Export Genesys CSV", html);
        Assert.Contains("Financial source reconciliation required", html);
        // Export links carry exactly the previewed filters (incl. the minimum), so the file matches the list.
        Assert.Contains("handler=Export", html); Assert.Contains("minAmount=250.5", html); Assert.Contains("towerId=7", html); Assert.Contains("dateFrom=2026-10-01", html);
        Assert.Contains("Tower 140 - Al Ghaf", html);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "does not have permission")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "not an empty result")]
    public async Task Errors_AreExplicit(HttpStatusCode status, string text)
    {
        var api = new FakeCollectionsApi { Status = status };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder")).Content.ReadAsStringAsync();
        Assert.Contains(text, html);
        Assert.DoesNotContain("Export review CSV", html);
    }

    [Theory]
    [InlineData(2028, 2, "2028-02-01", "2028-02-29")]
    [InlineData(2026, 12, "2026-12-01", "2026-12-31")]
    public async Task MonthAndYear_SetTheCampaignRangeToTheWholeMonth(int year, int month, string from, string to)
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        await client.GetAsync($"/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&month={month}&year={year}");
        var call = Assert.Single(api.Calls("/campaigns/preview"));
        Assert.Contains($"dateFrom={from}", call); Assert.Contains($"dateTo={to}", call);
    }

    [Fact]
    public async Task DownloadIsUtf8Csv_WithNoStore_PassesTheSameFilters_AndInvalidDateNeverCallsApi()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var download = await client.GetAsync("/Collections/Campaigns?handler=Export&stage=CurrentMonthReminder&mode=review&businessDate=2026-10-14&towerId=7&minAmount=500&dateFrom=2026-10-01&dateTo=2026-10-31&search=3001");
        Assert.Equal("text/csv", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.True(download.Headers.CacheControl!.NoStore);
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes.Take(3));
        var call = Assert.Single(api.Calls("/campaigns/export"));
        foreach (var expected in new[] { "towerId=7", "minAmount=500", "dateFrom=2026-10-01", "dateTo=2026-10-31", "search=3001", "mode=review" }) Assert.Contains(expected, call);
        var previews = api.Calls("/campaigns/preview").Count();
        var invalid = await client.GetAsync("/Collections/Campaigns?businessDate=invalid&render=full");
        Assert.Contains("Choose valid filters", await invalid.Content.ReadAsStringAsync());
        Assert.Equal(previews, api.Calls("/campaigns/preview").Count());   // an invalid date never reaches the API
    }

    [Fact]
    public async Task StaleSnapshot_ShowsTheWarningAndWithholdsBothExportLinks()
    {
        var api = new FakeCollectionsApi { Stale = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&towerId=7&businessDate=2026-10-14")).Content.ReadAsStringAsync();
        Assert.Contains("data-snapshot-warning", html); Assert.Contains("data-export-blocked", html); Assert.Contains("Export is disabled", html);
        Assert.DoesNotContain("Export review CSV", html); Assert.DoesNotContain("Export Genesys CSV", html);
        Assert.Contains("This data is stale", html);
    }

    [Fact]
    public async Task CoverageGap_ShowsTheBannerAndLoadAction_AndBlocksExport()
    {
        var api = new FakeCollectionsApi { Covered = false };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&businessDate=2026-03-15&dateFrom=2025-09-15&dateTo=2026-03-15")).Content.ReadAsStringAsync();
        Assert.Contains("data-coverage-gap", html); Assert.Contains("Load missing data", html); Assert.Contains("data-export-blocked", html);
        Assert.DoesNotContain("Export review CSV", html);
    }

    [Fact]
    public async Task BeforeTheFirstSnapshot_ThePageStillRendersWithALoadDataAction()
    {
        var api = new FakeCollectionsApi { NothingLoaded = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&dateFrom=2026-01-01&dateTo=2026-10-31")).Content.ReadAsStringAsync();
        Assert.Contains("No receivables data has been loaded yet.", html); Assert.Contains(">Load data<", html); Assert.Contains("data-export-blocked", html);
        Assert.Contains("not \"no units\"", html.Replace("&quot;", "\""));
        Assert.DoesNotContain("No units qualify for this stage", html);
    }
}

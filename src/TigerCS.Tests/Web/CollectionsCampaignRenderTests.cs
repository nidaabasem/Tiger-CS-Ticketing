extern alias TigerCsWeb;

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

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
        Assert.Contains("Minimum Total (AED)", html); Assert.Contains("name=\"minTotal\"", html); Assert.Contains("value=\"100\"", html);
        Assert.Contains("<option value=\"\">All months</option>", html);
        Assert.DoesNotContain("name=\"companyId\"", html);
        Assert.Contains("/js/collections-campaign-dates.js", html);
        Assert.DoesNotContain("page-subtitle", html);                                // no explanatory text on the page
        Assert.DoesNotContain("fully paid instalments are never reminder candidates", html);
    }

    [Fact]
    public async Task Results_SendEveryFilter_UseTheUnpaidDateLabel_AndOfferExportsFromTheSameFilters()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&towerId=7&businessDate=2026-10-14&dateFrom=2026-10-01&dateTo=2026-10-31&minTotal=250.5&search=3001")).Content.ReadAsStringAsync();
        var call = Assert.Single(api.Calls("/campaigns/preview"));
        foreach (var expected in new[] { "stage=CurrentMonthReminder", "towerId=7", "businessDate=2026-10-14", "dateFrom=2026-10-01", "dateTo=2026-10-31", "minTotal=250.5", "search=3001" }) Assert.Contains(expected, call);
        Assert.DoesNotContain("companyId", call);
        // Exactly the requested columns, in order.
        var headers = Regex.Matches(Regex.Match(html, "<thead>.*?</thead>", RegexOptions.Singleline).Value, "<th(?:\\s[^>]*)?>(.*?)</th>").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["Customer", "Mobile", "Email", "Tower", "Apartment", "Due", "Overdue", "Total"], headers);
        var cells = Regex.Matches(Regex.Match(html, "<tbody>.*?</tbody>", RegexOptions.Singleline).Value, "<td[^>]*>(.*?)</td>", RegexOptions.Singleline)
            .Select(m => System.Net.WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]+>", "")).Trim()).ToList();
        Assert.Equal(["Campaign Customer", "+971500003001", "— No email: email not possible", "Al Ghaf", "TP140-101", "—", "650.00", "650.00"], cells);   // no email -> a dash and the short reason; zero Due -> a dash; Total = Due + Overdue
        Assert.Contains("Export review CSV", html);
        Assert.DoesNotContain("Export Genesys CSV", html);
        foreach (var gone in new[] { "Earliest unpaid due date", "Qualifying balance", "Needs review", "Financial source reconciliation", "Data details", "Scheduled:", "Data refreshed", "campaign-summary", "Source:", "Preview only" })
            Assert.DoesNotContain(gone, html);
        // Export links carry exactly the previewed filters (incl. the minimum), so the file matches the list.
        Assert.Contains("handler=Export", html); Assert.Contains("minTotal=250.5", html); Assert.Contains("towerId=7", html); Assert.Contains("dateFrom=2026-10-01", html);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "does not have permission")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "campaign source is unavailable")]
    public async Task Errors_AreExplicit(HttpStatusCode status, string text)
    {
        var api = new FakeCollectionsApi { Status = status };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder")).Content.ReadAsStringAsync();
        Assert.Contains(text, html);
        Assert.DoesNotContain("Export review CSV", html);
    }

    [Theory]
    [InlineData(2026, 2, "2026-02-01", "2026-02-28")]
    [InlineData(2026, 9, "2026-09-01", "2026-09-30")]
    public async Task MonthAndYear_SetTheCampaignRangeToTheWholeMonth(int year, int month, string from, string to)
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        await client.GetAsync($"/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&month={month}&year={year}");
        var call = Assert.Single(api.Calls("/campaigns/preview"));
        Assert.Contains($"dateFrom={from}", call); Assert.Contains($"dateTo={to}", call);
    }

    [Fact]
    public async Task MinimumTotal_DefaultsTo100_CanBeCleared_AndAFutureMonthMakesNoDataCall()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder");
        await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&minTotal=");
        var calls = api.Calls("/campaigns/preview").ToList();
        Assert.Contains("minTotal=100", calls[0]); Assert.DoesNotContain("minTotal", calls[1]);
        var shell = await (await client.GetAsync("/Collections/Campaigns?stage=CurrentMonthReminder&minTotal=")).Content.ReadAsStringAsync();
        Assert.Matches("<input[^>]*name=\"minTotal\"[^>]*value=\"\"", shell);
        api.Requests.Clear();
        var next = new DateOnly(DateTime.UtcNow.AddHours(4).Year + 1, 3, 1);
        var html = await (await client.GetAsync($"/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&month=3&year={next.Year}")).Content.ReadAsStringAsync();
        Assert.Empty(api.Calls("/campaigns/preview")); Assert.Contains("Nothing is due in this period yet.", html);
    }

    [Fact]
    public async Task DownloadIsUtf8Csv_WithNoStore_PassesTheSameFilters_AndInvalidDateNeverCallsApi()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var download = await client.GetAsync("/Collections/Campaigns?handler=Export&stage=CurrentMonthReminder&mode=review&businessDate=2026-10-14&towerId=7&minTotal=500&dateFrom=2026-10-01&dateTo=2026-10-31&search=3001");
        Assert.Equal("text/csv", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.True(download.Headers.CacheControl!.NoStore);
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes.Take(3));
        var call = Assert.Single(api.Calls("/campaigns/export"));
        foreach (var expected in new[] { "towerId=7", "minTotal=500", "dateFrom=2026-10-01", "dateTo=2026-10-31", "search=3001", "mode=review" }) Assert.Contains(expected, call);
        var previews = api.Calls("/campaigns/preview").Count();
        var invalid = await client.GetAsync("/Collections/Campaigns?businessDate=invalid&render=full");
        Assert.Contains("Choose valid filters", await invalid.Content.ReadAsStringAsync());
        Assert.Equal(previews, api.Calls("/campaigns/preview").Count());   // an invalid date never reaches the API
    }

    [Fact]
    public async Task StaleSnapshot_ShowsOneShortLine_AndWithholdsBothExportLinks()
    {
        var api = new FakeCollectionsApi { Stale = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&towerId=7&businessDate=2026-10-14")).Content.ReadAsStringAsync();
        Assert.Contains("Could not load data for <strong>Tiger Group Dubai</strong>.", html);
        Assert.Contains(">Retry<", html);
        Assert.DoesNotContain("Export review CSV", html); Assert.DoesNotContain("Export Genesys CSV", html);   // an export of old data stays blocked
        foreach (var gone in new[] { "Export is disabled", "This data is stale", "data-snapshot-warning", "data-export-blocked", "Data details" }) Assert.DoesNotContain(gone, html);
    }

    [Fact]
    public async Task CoverageGap_ShowsOneShortLine_AndBlocksExport()
    {
        var api = new FakeCollectionsApi { Covered = false };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&businessDate=2026-03-15&dateFrom=2025-09-15&dateTo=2026-03-15")).Content.ReadAsStringAsync();
        Assert.Contains("Could not load data for <strong>Tiger Group Dubai</strong>.", html); Assert.Contains("data-load-button", html);
        Assert.DoesNotContain("Export review CSV", html);
        Assert.DoesNotContain("data-coverage-gap", html); Assert.DoesNotContain("Load missing data", html);
    }

    [Fact]
    public async Task BeforeTheFirstSnapshot_ThePageStillRenders_WithTheShortLineAndRetry()
    {
        var api = new FakeCollectionsApi { NothingLoaded = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&dateFrom=2026-01-01&dateTo=2026-10-31")).Content.ReadAsStringAsync();
        Assert.Contains("Could not load data for <strong>Tiger Group Dubai</strong>.", html); Assert.Contains(">Retry<", html);
        Assert.DoesNotContain("Export review CSV", html);
        Assert.DoesNotContain("No receivables data has been loaded yet.", html);
    }

    [Fact]
    public async Task WhenSharjahFails_TheLineNamesOnlySharjah_RetryReloadsIt_AndTheDubaiRowsStillShow()
    {
        var api = new FakeCollectionsApi { SharjahFailed = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&businessDate=2026-10-14")).Content.ReadAsStringAsync();
        var line = Regex.Match(html, "load-problem.*?</form>", RegexOptions.Singleline).Value;
        Assert.Contains("Could not load data for <strong>Tiger Group Sharjah</strong>.", line);
        Assert.DoesNotContain("Dubai", line);
        Assert.Contains("name=\"companyId\" value=\"32\"", line); Assert.Contains(">Retry<", line);
        Assert.Single(Regex.Matches(html, "data-load-problem"));
        Assert.Contains("Campaign Customer", html);
        Assert.DoesNotContain("Export review CSV", html);                          // incomplete data is never exported
        foreach (var gone in new[] { "Export is disabled", "Data details", "error 7399", "consecutive" }) Assert.DoesNotContain(gone, html);
    }
}

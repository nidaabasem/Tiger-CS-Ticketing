extern alias TigerCsWeb;

using System.Text.RegularExpressions;

namespace TigerCS.Tests.Web;

/// <summary>The redesigned Campaigns filters reach the API unchanged for the preview, every page and both exports.</summary>
public sealed class CollectionsCampaignFilterTests
{
    private const string Filters = "stage=LegalNotice&towerId=7&businessDate=2026-10-14&dateFrom=2026-09-01&dateTo=2026-10-31&minTotal=250&paymentStatus=Overdue&search=3001";

    [Fact]
    public async Task EveryFilter_ReachesThePreviewCall_TogetherWithThePaymentStatus()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        await client.GetAsync($"/Collections/Campaigns?handler=Results&{Filters}");
        var call = Assert.Single(api.Calls("/campaigns/preview"));
        foreach (var expected in new[] { "stage=LegalNotice", "towerId=7", "businessDate=2026-10-14", "dateFrom=2026-09-01", "dateTo=2026-10-31", "minTotal=250", "paymentStatus=Overdue", "search=3001" })
            Assert.Contains(expected, call);
    }

    [Fact]
    public async Task PaymentStatus_DefaultsToBoth_AndIsNeverDropped()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&businessDate=2026-10-14")).Content.ReadAsStringAsync();
        Assert.Contains("Both", Assert.Single(api.Calls("/campaigns/preview")).Split('?')[1].Split('&').Single(p => p.StartsWith("paymentStatus=", StringComparison.Ordinal)));
        // Paging and exports keep it.
        Assert.Matches("paymentStatus=Both", html);
    }

    [Fact]
    public async Task ExportLinksAndExports_UseExactlyThePreviewFilters()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync($"/Collections/Campaigns?handler=Results&{Filters}")).Content.ReadAsStringAsync();
        var link = System.Net.WebUtility.HtmlDecode(Regex.Match(html, "href=\"([^\"]*handler=Export[^\"]*mode=review[^\"]*)\"").Groups[1].Value);
        Assert.Contains("paymentStatus=Overdue", link); Assert.Contains("minTotal=250", link); Assert.Contains("towerId=7", link);
        Assert.Contains("dateFrom=2026-09-01", link); Assert.Contains("dateTo=2026-10-31", link);

        await client.GetAsync(link);
        var export = Assert.Single(api.Calls("/campaigns/export"));
        var preview = Assert.Single(api.Calls("/campaigns/preview"));
        string[] Filter(string call) => call.Split('?')[1].Split('&').Where(p => !p.StartsWith("page=", StringComparison.Ordinal) && !p.StartsWith("mode=", StringComparison.Ordinal)).Order().ToArray();
        Assert.Equal(Filter(preview), Filter(export));          // the same filters, parameter for parameter
    }

    [Fact]
    public async Task LastSixMonths_StaysAShortcut_AndKeepsThePaymentStatusAndTower()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync($"/Collections/Campaigns?{Filters}")).Content.ReadAsStringAsync();
        var link = System.Net.WebUtility.HtmlDecode(Regex.Match(html, "<a[^>]*href=\"([^\"]*)\"[^>]*data-last-six-months").Groups[1].Value);
        Assert.Contains("paymentStatus=Overdue", link); Assert.Contains("towerId=7", link); Assert.Contains("dateFrom=", link); Assert.Contains("dateTo=", link);
    }

    [Fact]
    public async Task TheFilterBar_HasNoMonthOrYear_AndOneOfEachOtherControl()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?stage=CurrentMonthReminder")).Content.ReadAsStringAsync();
        var form = Regex.Match(html, "<form class=\"filter-bar\".*?</form>", RegexOptions.Singleline).Value;
        foreach (var name in new[] { "stage", "towerId", "paymentStatus", "dateFrom", "dateTo", "minTotal", "businessDate", "search" })
            Assert.Single(Regex.Matches(form, $"name=\"{name}\""));                  // no duplicate filters
        Assert.DoesNotContain("name=\"month\"", form); Assert.DoesNotContain("name=\"year\"", form);
        foreach (var option in new[] { "Due and Overdue (both)", ">Due<", ">Overdue<" }) Assert.Contains(option, form);
    }

    [Fact]
    public async Task OldMonthYearBookmarks_StillBecomeTheWholeMonthRange()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&month=9&year=2026");
        var call = Assert.Single(api.Calls("/campaigns/preview"));
        Assert.Contains("dateFrom=2026-09-01", call); Assert.Contains("dateTo=2026-09-30", call);
    }

    [Fact]
    public async Task ReviewAndApprove_CarriesTheStageDatesAndPaymentStatus()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync($"/Collections/Campaigns?{Filters}")).Content.ReadAsStringAsync();
        var link = System.Net.WebUtility.HtmlDecode(Regex.Match(html, "href=\"([^\"]*)\"[^>]*data-review-link").Groups[1].Value);
        Assert.StartsWith("/Collections/Review?", link);
        Assert.Contains("reminderType=LegalNotice", link); Assert.Contains("towerId=7", link); Assert.Contains("dueFrom=2026-09-01", link); Assert.Contains("dueTo=2026-10-31", link); Assert.Contains("dueStatus=Overdue", link);
    }

    [Fact]
    public async Task TheResults_NameTheGenesysCampaignExactlyAsTheStage()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=LegalNotice&businessDate=2026-10-14")).Content.ReadAsStringAsync();
        Assert.Contains("data-campaign-name>Legal notice - days 12 and 14<", html);
        var page = await (await client.GetAsync("/Collections/Campaigns?stage=LegalNotice")).Content.ReadAsStringAsync();
        Assert.Contains(">Legal notice - days 12 and 14</option>", page);                // the dropdown shows the same text
    }
}

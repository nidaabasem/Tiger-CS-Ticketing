using System.Text.RegularExpressions;

namespace TigerCS.Tests.Web;

/// <summary>The Status poll handler and the per-company load states the Receivables / Campaigns pages render.</summary>
public sealed class ReceivablesLoadStateRenderTests
{
    [Theory]
    [InlineData("/Collections/Receivables?handler=Status")]
    [InlineData("/Collections/Campaigns?handler=Status")]
    public async Task TheStatusHandlerAnswersWithTinyJson_AndNeverReadsTheList(string url)
    {
        var api = new FakeCollectionsApi { DubaiLoadingOnly = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var response = await client.GetAsync(url);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("application/json", response.Content.Headers.ContentType!.ToString());
        Assert.Contains("\"active\":true", json);
        Assert.True(json.Length < 1500, $"{json.Length} bytes");
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Empty(api.Calls("receivables/instalments"));           // the instalment list is never touched by a poll
        Assert.Empty(api.Calls("/campaigns"));
        Assert.Empty(api.Calls("coverage/load"));                     // and a poll never starts a refresh
    }

    [Fact]
    public async Task TheStatusHandlerSaysNotActive_WhenNothingRuns()
    {
        var api = new FakeCollectionsApi { SharjahFailed = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var json = await (await client.GetAsync("/Collections/Receivables?handler=Status")).Content.ReadAsStringAsync();
        Assert.Contains("\"active\":false", json);
        Assert.Contains("\"state\":\"Failed\"", json);
    }

    [Fact]
    public async Task EachFailedCompanyGetsItsOwnRetry_AndNoPermanentLoadingNoticeAppears()
    {
        var api = new FakeCollectionsApi { NothingLoaded = true, SharjahFailed = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("data-load-running", html);
        Assert.Single(Regex.Matches(html, "data-load-problem"));
        Assert.Equal(2, Regex.Matches(html, "data-load-button").Count);
        Assert.Contains("name=\"companyId\" value=\"4\"", html);
        Assert.Contains("name=\"companyId\" value=\"32\"", html);
        Assert.Contains("Not loaded yet. This is not an empty result.", html);
        Assert.Contains("while reading from PACT", html);                       // the failing phase, from the recorded error
        Assert.Contains("linked server", html);
        Assert.DoesNotContain("10.10.10", html);
    }

    [Fact]
    public async Task StaleDataIsLabelledStale_WithItsTimestamp_NotAsAFailure()
    {
        var api = new FakeCollectionsApi { Stale = true };
        api.Rows.Add(FakeCollectionsApi.Unpaid(new DateOnly(2026, 8, 5), 300m, "INV-1"));
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results")).Content.ReadAsStringAsync();
        Assert.Contains("is out of date", html);
        Assert.Contains("(Dubai)", html);                                       // the last refresh time
        Assert.DoesNotContain("Could not load data for", html);
        Assert.Contains("data-unit-row", html);                                 // the last valid data stays visible
    }

    [Fact]
    public async Task ThePagesLoadTheMonitorBeforeTheirOwnScript()
    {
        using var factory = CollectionsWebHost.Factory(new FakeCollectionsApi()); using var client = factory.CreateClient();
        foreach (var page in new[] { "/Collections/Receivables", "/Collections/Campaigns" })
        {
            var html = await (await client.GetAsync(page)).Content.ReadAsStringAsync();
            Assert.True(html.IndexOf("receivables-load-monitor.js", StringComparison.Ordinal) is var m and > 0 && m < html.IndexOf("/js/receivables.js", StringComparison.Ordinal), page);
        }
    }
}

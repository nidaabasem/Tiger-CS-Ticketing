extern alias TigerCsWeb;

using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Display = TigerCsWeb::TigerCS.Web.Pages.Collections.CollectionsDisplay;

namespace TigerCS.Tests.Web;

/// <summary>Coverage-gap banner, load action (incl. before the first snapshot), "Last 6 months" and the "Load data" POST.</summary>
public sealed class CollectionsCoverageRenderTests
{
    [Fact]
    public async Task ReceivablesResults_ShowTheGap_TheLoadButton_AndNeverACompleteLookingZero()
    {
        var api = new FakeCollectionsApi { Covered = false };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results&dateFrom=2025-09-15&dateTo=2026-03-15")).Content.ReadAsStringAsync();
        Assert.Contains("Could not load data for <strong>Tiger Group Dubai</strong>.", html);
        Assert.Contains("data-load-button", html); Assert.Contains(">Retry<", html);
        Assert.Contains("__RequestVerificationToken", html);
        Assert.DoesNotContain("data-coverage-gap", html);
        Assert.DoesNotContain("No units match these filters.", html);       // a gap is reported, never shown as an empty result
    }

    [Fact]
    public async Task LastSixMonthsShortcut_IsALinkWithTheExplicitRange_OnCampaigns()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var campaigns = await (await client.GetAsync("/Collections/Campaigns?stage=CurrentMonthReminder&businessDate=2026-03-15&towerId=7")).Content.ReadAsStringAsync();
        Assert.Contains("dateFrom=2025-09-15", campaigns); Assert.Contains("dateTo=2026-03-15", campaigns); Assert.Contains("businessDate=2026-03-15", campaigns); Assert.Contains("minTotal=100", campaigns);
    }

    [Fact]
    public async Task WhileALoadRuns_TheButtonIsReplacedByAProgressNotice_ThatThePageKeepsPolling()
    {
        var api = new FakeCollectionsApi { Covered = false, Loading = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results&dateFrom=2025-09-15&dateTo=2026-03-15")).Content.ReadAsStringAsync();
        Assert.Contains("data-load-running", html);
        Assert.DoesNotContain("data-load-button", html);
    }

    [Fact]
    public async Task LoadData_PostsToTheApi_AndRedirectsBackToTheSameFiltersWithANotice()
    {
        var api = new FakeCollectionsApi { Covered = false };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var html = await (await client.GetAsync("/Collections/Receivables?render=full&towerId=7&dateFrom=2025-09-15&dateTo=2026-03-15")).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.True(token.Length > 20);
        var post = await client.PostAsync("/Collections/Receivables?handler=LoadCoverage", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["dateFrom"] = "2025-09-15", ["dateTo"] = "2026-03-15", ["returnUrl"] = "/Collections/Receivables?towerId=7&dateFrom=2025-09-15&dateTo=2026-03-15", ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal("/Collections/Receivables?towerId=7&dateFrom=2025-09-15&dateTo=2026-03-15&load=started", post.Headers.Location!.OriginalString);
        Assert.Contains("POST /api/collections/receivables/coverage/load?dateFrom=2025-09-15&dateTo=2026-03-15", api.Requests);
        var after = await (await client.GetAsync(post.Headers.Location.OriginalString + "&handler=Results")).Content.ReadAsStringAsync();
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
}

extern alias TigerCsWeb;

using TigerCS.Application.Modules.Collections.Reconciliation;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Web;

/// <summary>The reconciliation diagnostics page and the "not reconciled" banner of the Campaigns snapshot box.</summary>
public sealed class ReconciliationRenderTests
{
    private static ReconciliationOverviewDto Failed()
    {
        var diff = new ReconUnitDiff("T1", 11, "TP140-101", 1000m, 400m, 1, 1, 0m, 0m, 1000m, 400m,
            [new(ReconDifferenceKinds.AmountMismatch, "V1", new(2026, 9, 5), new(2026, 9, 5), 1000m, 400m, false)], "1 AmountMismatch.");
        var cmp = new ReconComparison(10, 10, 12, 12, 5000m, 4400m, 9, 1, 0, 0, 600m, [diff], false);
        var run = new ReconciliationRunDto(Guid.NewGuid(), "Failed", DateTime.UtcNow.AddMinutes(-3), DateTime.UtcNow.AddMinutes(-2), new(2026, 1, 1), new(2026, 10, 31), null, 0.01m, null,
            [new(4, "Tiger Group Dubai", "Failed", "1 of 10 units differ.", DateTime.UtcNow.AddMinutes(-3), DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(-10), "dbo.p4AccountReceivables", cmp)]);
        return new(new(false, "EvidenceFailed", "The reconciliation found differences between the snapshot and PACT for Tiger Group Dubai (1 unit(s)); see the diagnostics.", run.FinishedUtc, 1, true, "TICKET-1"), run, [run]);
    }

    [Fact]
    public async Task ThePageNamesTheFailingUnit_AndWhy()
    {
        var api = new FakeCollectionsApi { Reconciliation = Failed() };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Reconciliation")).Content.ReadAsStringAsync();
        Assert.Contains("data-source-gate=\"EvidenceFailed\"", html);
        Assert.Contains("TP140-101", html);
        Assert.Contains("Different remaining amount", html);
        Assert.Contains("1,000.00", html); Assert.Contains("400.00", html);
        Assert.Contains("TICKET-1", html);
        Assert.Empty(api.Calls("/reconciliation/run"));                  // opening the page never starts a run
    }

    [Fact]
    public async Task ThePageSaysPlainly_ThatMatchingReadsOfOneProcedureAreNotAnIndependentVerification()
    {
        using var factory = CollectionsWebHost.Factory(new FakeCollectionsApi { Reconciliation = Failed() }); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Reconciliation")).Content.ReadAsStringAsync();
        Assert.Contains("data-reconciliation-basis", html);
        Assert.Contains("Snapshot consistency", html);
        Assert.Contains("Ledger correctness", html);
        Assert.Contains("not</em> an independent financial verification", html);
        Assert.Contains("usp_CollectionsRcv_Refresh", html);                  // names the snapshot side
        Assert.Contains("dbo.p4AccountReceivables", html);                    // names the compared side
    }

    [Fact]
    public async Task NeverRun_IsShownAsMissingEvidence_NotAsADataProblem()
    {
        using var factory = CollectionsWebHost.Factory(new FakeCollectionsApi()); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Reconciliation")).Content.ReadAsStringAsync();
        Assert.Contains("data-source-gate=\"EvidenceNotRun\"", html);
        Assert.Contains("data-never-run", html);
        Assert.DoesNotContain("data-differences", html);
    }

    [Fact]
    public async Task RunningRefreshesItself_ButOnlyWhileRunning()
    {
        var running = new ReconciliationRunDto(Guid.NewGuid(), "Running", DateTime.UtcNow, null, new(2026, 1, 1), new(2026, 10, 31), null, 0.01m, null, []);
        var api = new FakeCollectionsApi { Reconciliation = new(new(false, "EvidenceNotRun", "x", null, 0, true, null), running, [running]) };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Reconciliation")).Content.ReadAsStringAsync();
        Assert.Contains("http-equiv=\"refresh\"", html);
        using var idle = CollectionsWebHost.Factory(new FakeCollectionsApi()); using var idleClient = idle.CreateClient();
        Assert.DoesNotContain("http-equiv=\"refresh\"", await (await idleClient.GetAsync("/Collections/Reconciliation")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheCampaignBanner_KeepsTheRequiredSentence_AddsTheSpecificReason_AndLinksToTheDiagnostics()
    {
        var api = new FakeCollectionsApi { SourceUnreconciled = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Campaigns?handler=Results&stage=CurrentMonthReminder&businessDate=2026-10-14")).Content.ReadAsStringAsync();
        Assert.Contains("The financial source has not been reconciled against PACT for the configured procedure, so nothing can be approved or sent.", html);
        Assert.Contains("data-reconciliation-link", html);
        Assert.Contains("/Collections/Reconciliation", html);
    }
}

/// <summary>Stage schedule restrictions are explained with the real send days, and never overridden.</summary>
public sealed class StageScheduleMessageTests
{
    private static string? Message(string stage, DateOnly[] scheduled, DateOnly preview, DateOnly today, bool isScheduled) =>
        TigerCsWeb::TigerCS.Web.Pages.Collections.CampaignsModel.ScheduleRestriction(stage, scheduled, preview, today, isScheduled);

    [Fact]
    public void TodayIsNotTheStagesSendDay_NamesTheStageAndItsDay()
    {
        var text = Message("Current month reminder - day 14", [new(2026, 10, 14)], new(2026, 10, 22), new(2026, 10, 22), false)!;
        Assert.Contains("Current month reminder - day 14", text);
        Assert.Contains("14 Oct 2026", text);
        Assert.Contains("22 Oct 2026", text);
        Assert.Contains("cannot be sent outside the schedule", text);
    }

    [Fact]
    public void APreviewDateThatIsNotToday_SaysSo_EvenOnAScheduledDay()
    {
        var text = Message("Current month reminder - day 14", [new(2026, 10, 14)], new(2026, 10, 14), new(2026, 10, 22), true)!;
        Assert.Contains("is not today", text);
    }

    [Fact]
    public void OnTheScheduledDay_ForToday_ThereIsNoRestriction() =>
        Assert.Null(Message("Current month reminder - day 14", [new(2026, 10, 14)], new(2026, 10, 14), new(2026, 10, 14), true));

    [Fact]
    public void TwoSendDays_AreBothNamed() =>
        Assert.Contains("12 Oct 2026 and 14 Oct 2026", Message("Legal notice - days 12 and 14", [new(2026, 10, 12), new(2026, 10, 14)], new(2026, 10, 22), new(2026, 10, 22), false));
}

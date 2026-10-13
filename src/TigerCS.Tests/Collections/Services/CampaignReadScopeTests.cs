using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;

namespace TigerCS.Tests.Collections.Services;

/// <summary>
/// "All months" on Campaigns reads the whole history up to today (like Receivables) WITHOUT changing the stage policy: the read scope only feeds the Due / Overdue / Total
/// figures, the Minimum Total and the ordering; which units are candidates, their stage amount, flags and review reasons come from the eligibility scope (the configured start
/// date, or the explicit From), exactly as before.
/// </summary>
public sealed class CampaignReadScopeTests
{
    private static PactReceivableInstalment Pay(string tenant, int unit, DateTime due, decimal amount, string voucher, string phone = "971500003001") =>
        new(4, tenant, "Customer " + tenant, phone, "x@example.test", unit, $"TP140-{unit}", "TP140", voucher, "", due, amount, "Installment");

    private static CollectionsCampaignAppServiceTests.Harness Build()
    {
        var h = new CollectionsCampaignAppServiceTests.Harness(); h.Campaign.FinancialSourceValidated = true;
        h.Source.Items.AddRange([
            Pay("A", 1, new(2025, 3, 10), 300m, "A-old"),     // a previous YEAR: before the configured start date (2026-01-01)
            Pay("A", 1, new(2026, 3, 10), 200m, "A-new"),     // inside the stage policy scope: the only row that decides eligibility / the stage amount
            Pay("B", 2, new(2025, 5, 1), 900m, "B-old"),      // a unit with ONLY pre-start history: never a candidate, however far the read goes
            Pay("C", 3, new(2026, 3, 11), 150m, "C-new")]);
        return h;
    }

    [Fact]
    public async Task AllMonths_ReadsTheWholeHistory_ButEligibilityAndTheStageAmountStayOnTheConfiguredScope()
    {
        var h = Build();
        var report = (await h.Service.PreviewAsync(h.Manager, "OverdueReminder", minTotal: null)).Value!;
        Assert.Equal(new DateOnly(2000, 1, 1), h.Source.LastRequest!.FromDate);                         // the READ reaches previous years ...
        Assert.Equal(["A", "C"], report.Items.Where(i => i.Status != "NotEligible").Select(i => i.TenantId).Order());      // ... but B (only pre-start history) is not eligible
        Assert.Equal("NotEligible", report.Items.Single(i => i.TenantId == "B").Status);                 // it is still LISTED with its real balances
        var a = report.Items.Single(i => i.TenantId == "A");
        Assert.Equal(200m, a.Amount);                                                                    // the STAGE amount: only the eligible row (not 500)
        Assert.Equal((0m, 500m, 500m), (a.DueAmount, a.OverdueAmount, a.TotalAmount));                   // the figures shown: all history up to today, like Receivables
        Assert.Equal(new DateOnly(2026, 3, 10), a.DueDate);                                              // earliest ELIGIBLE due date
    }

    [Fact]
    public async Task TheStagePolicyIsUnchanged_WhateverTheReadScope()
    {
        // The same units/amount/status/reason with All months (reads from 2000) and with the explicit From the policy has always used (the configured start date).
        var all = (await Build().Service.PreviewAsync(Build().Manager, "OverdueReminder", minTotal: null)).Value!;
        var scoped = (await Build().Service.PreviewAsync(Build().Manager, "OverdueReminder", dateFrom: new DateOnly(2026, 1, 1), minTotal: null)).Value!;
        static object Policy(TigerCS.Application.Modules.Collections.Dto.CollectionsCampaignContactDto i) => new { i.TenantId, i.Amount, i.Status, i.Reason, i.DueDate, i.RecordId };
        Assert.Equal(scoped.Items.Where(i => i.Status != "NotEligible").OrderBy(i => i.TenantId).Select(Policy), all.Items.Where(i => i.Status != "NotEligible").OrderBy(i => i.TenantId).Select(Policy));
        Assert.Equal((scoped.ReadyCount, scoped.ReviewCount), (all.ReadyCount, all.ReviewCount));
        Assert.Equal(scoped.Items.Count(i => i.Status != "NotEligible"), all.Items.Count(i => i.Status != "NotEligible"));
        // Only the Due / Overdue / Total columns differ, and only by the pre-start payments of an eligible unit.
        Assert.Equal(200m, scoped.Items.Single(i => i.TenantId == "A").TotalAmount);
        Assert.Equal(500m, all.Items.Single(i => i.TenantId == "A").TotalAmount);
        Assert.Equal(150m, all.Items.Single(i => i.TenantId == "C").TotalAmount);
    }

    [Fact]
    public async Task AnExplicitFrom_StillWidensTheEligibility_AsItAlwaysDid()
    {
        var h = Build();
        var report = (await h.Service.PreviewAsync(h.Manager, "OverdueReminder", dateFrom: new DateOnly(2025, 1, 1), minTotal: null)).Value!;
        Assert.Equal(["A", "B", "C"], report.Items.Select(i => i.TenantId).Order());
        Assert.Equal(500m, report.Items.Single(i => i.TenantId == "A").Amount);
    }

    [Fact]
    public async Task MinimumTotal_UsesTheShownTotal_AndTheOrderIsTowerThenUnit_ForPreviewAndExport()
    {
        var h = Build();
        var report = (await h.Service.PreviewAsync(h.Manager, "OverdueReminder", minTotal: 200m)).Value!;
        Assert.Equal(["A"], report.Items.Where(i => i.Status != "NotEligible").Select(i => i.TenantId));    // A shows 500 (> 200); C shows 150
        var all = (await h.Service.PreviewAsync(h.Manager, "OverdueReminder", minTotal: null)).Value!;
        Assert.Equal(["A", "C"], all.Items.Where(i => i.Status != "NotEligible").Select(i => i.TenantId));    // one tower (TP140): unit 1 before unit 3, whatever the payment dates
        var export = (await h.Service.ExportAsync(h.Manager, "OverdueReminder", "review", minTotal: 200m)).Value!;
        Assert.Equal(2, export.RowCount);                                                                 // A (eligible) and B (listed, NotEligible): the file is exactly the previewed rows
    }
}

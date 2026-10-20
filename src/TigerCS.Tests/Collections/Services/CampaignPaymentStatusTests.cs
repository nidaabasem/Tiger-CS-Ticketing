using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Services;

/// <summary>Payment status (Due / Overdue / Both): one rule for the preview, the counts, the qualifying amount, paging and both CSV exports.</summary>
public sealed class CampaignPaymentStatusTests
{
    private const string Stage = "CurrentMonthReminder";

    /// <summary>Clock = 2026-10-14. Unit 101 owes October (Due 500) and September (Overdue 300); unit 102 only October (Due 400); unit 103 only September (Overdue 600).</summary>
    private static CollectionsCampaignAppServiceTests.Harness Seeded()
    {
        var h = new CollectionsCampaignAppServiceTests.Harness();
        h.Campaign.FinancialSourceValidated = true;
        h.Source.Items.Add(CollectionsCampaignAppServiceTests.Row(day: 10, amount: 500m, tenant: "3001", unit: 101));
        h.Source.Items.Add(CollectionsCampaignAppServiceTests.Row(day: 5, amount: 300m, tenant: "3001", unit: 101) with { DueDate = new(2026, 9, 5) });
        h.Source.Items.Add(CollectionsCampaignAppServiceTests.Row(day: 12, amount: 400m, tenant: "3002", unit: 102));
        h.Source.Items.Add(CollectionsCampaignAppServiceTests.Row(day: 3, amount: 600m, tenant: "3003", unit: 103) with { DueDate = new(2026, 9, 3) });
        return h;
    }

    private static string[] Units(CollectionsCampaignPreviewDto report) => report.Items.Select(i => i.UnitCode).Order().ToArray();

    [Fact]
    public async Task Both_IsTheDefault_AndListsEveryUnitWithDueOrOverdue()
    {
        var h = Seeded();
        var byDefault = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 0m)).Value!;
        var both = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 0m, paymentStatus: "Both")).Value!;
        Assert.Equal(["TP140-101", "TP140-102", "TP140-103"], Units(byDefault));
        Assert.Equal(Units(byDefault), Units(both));
        Assert.Equal("Both", both.PaymentStatus);
        var unit101 = both.Items.Single(i => i.UnitCode == "TP140-101");
        Assert.Equal((500m, 300m, 800m), (unit101.DueAmount, unit101.OverdueAmount, unit101.TotalAmount));
    }

    [Fact]
    public async Task Due_KeepsOnlyUnitsWithDueInstalments_AndOnlyTheirDueAmount()
    {
        var h = Seeded();
        var due = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 0m, paymentStatus: "due")).Value!;
        Assert.Equal(["TP140-101", "TP140-102"], Units(due));
        Assert.All(due.Items, i => Assert.Equal(0m, i.OverdueAmount));
        Assert.Equal(500m, due.Items.Single(i => i.UnitCode == "TP140-101").TotalAmount);   // the unit's overdue 300 is not counted under Due
        Assert.Equal("Due", due.PaymentStatus);
        Assert.Equal(2, due.TotalCount);
    }

    [Fact]
    public async Task Overdue_KeepsOnlyUnitsWithOverdueInstalments_AndOnlyTheirOverdueAmount()
    {
        var h = Seeded();
        var overdue = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 0m, paymentStatus: "Overdue")).Value!;
        Assert.Equal(["TP140-101", "TP140-103"], Units(overdue));
        Assert.All(overdue.Items, i => Assert.Equal(0m, i.DueAmount));
        Assert.Equal(300m, overdue.Items.Single(i => i.UnitCode == "TP140-101").TotalAmount);
    }

    [Fact]
    public async Task MinimumTotal_IsAppliedToTheSelectedStatusOnly()
    {
        var h = Seeded();
        // Unit 101's Total is 800 under Both but 500 / 300 under a single status, unit 102 owes 400: the minimum is judged on what is listed.
        var both = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 450m, paymentStatus: "Both")).Value!;
        var overdue = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 450m, paymentStatus: "Overdue")).Value!;
        Assert.Equal(["TP140-101", "TP140-103"], Units(both));
        Assert.Equal(["TP140-103"], Units(overdue));
    }

    [Theory]
    // Current-month stage: only units with a current-month instalment qualify (101, 102); unit 103 is listed but NotEligible, so its 600 is not "qualifying".
    [InlineData("Both", 3, 2, 1200)]
    [InlineData("Due", 2, 2, 900)]
    [InlineData("Overdue", 2, 1, 300)]
    public async Task EligibleCountAndQualifyingAmount_CoverTheWholeFilteredList_NotJustThePage(string status, int count, int eligible, int amount)
    {
        var h = Seeded();
        var page = (await h.Service.PreviewAsync(h.Manager, Stage, page: 1, pageSize: 1, minTotal: 0m, paymentStatus: status)).Value!;
        Assert.Equal(count, page.TotalCount);
        Assert.Single(page.Items);                                   // paging happens after the filter
        Assert.Equal(eligible, page.TotalCount - page.NotEligibleCount);
        Assert.Equal(amount, (int)page.EligibleTotalAmount!.Value);
    }

    [Fact]
    public async Task TheStageRule_IsUntouchedByPaymentStatus()
    {
        var h = Seeded();
        // Stage eligibility looks at the stage's own rows, whatever the status filter shows: a unit has the same readiness under every status.
        var both = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 0m, paymentStatus: "Both")).Value!;
        var due = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 0m, paymentStatus: "Due")).Value!;
        foreach (var row in due.Items)
        {
            var same = both.Items.Single(b => b.UnitCode == row.UnitCode);
            Assert.Equal((same.Status, same.Reason, same.Amount), (row.Status, row.Reason, row.Amount));
        }
    }

    [Theory]
    [InlineData("Both")]
    [InlineData("Due")]
    [InlineData("Overdue")]
    public async Task BothCsvExports_ContainExactlyThePreviewedUnits(string status)
    {
        var h = Seeded();
        var preview = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 0m, paymentStatus: status)).Value!;
        var review = (await h.Service.ExportAsync(h.Manager, Stage, "review", minTotal: 0m, paymentStatus: status)).Value!;
        Assert.Equal(preview.TotalCount, review.RowCount);
        foreach (var unit in preview.Items.Select(i => i.UnitCode)) Assert.Contains(unit, review.Csv);
        foreach (var unit in new[] { "TP140-101", "TP140-102", "TP140-103" }.Except(preview.Items.Select(i => i.UnitCode)))
            Assert.DoesNotContain(unit, review.Csv);
    }

    [Theory]
    [InlineData("Late")]
    [InlineData("Due,Overdue")]
    public async Task AnUnknownPaymentStatus_IsRefused_WithoutReadingTheSource(string status)
    {
        var h = Seeded();
        var preview = await h.Service.PreviewAsync(h.Manager, Stage, paymentStatus: status);
        Assert.Equal(CollectionsOutcome.InvalidRequest, preview.Outcome);
        Assert.Equal(0, h.Source.Reads);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ExportAsync(h.Manager, Stage, "review", paymentStatus: status)).Outcome);
    }

    [Fact]
    public void Parser_AcceptsBlankBothAndAll_AndIsCaseInsensitive()
    {
        foreach (var blank in new string?[] { null, "", " ", "both", "ALL" }) { Assert.True(CampaignPaymentFilters.TryParse(blank, out var f)); Assert.Equal(CampaignPaymentFilter.Both, f); }
        Assert.True(CampaignPaymentFilters.TryParse("dUe", out var due)); Assert.Equal(CampaignPaymentFilter.Due, due);
        Assert.True(CampaignPaymentFilters.TryParse("OVERDUE", out var overdue)); Assert.Equal(CampaignPaymentFilter.Overdue, overdue);
        Assert.False(CampaignPaymentFilters.TryParse("paid", out _));
    }
}

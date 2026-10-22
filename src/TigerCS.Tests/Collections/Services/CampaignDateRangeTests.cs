using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;

namespace TigerCS.Tests.Collections.Services;

/// <summary>
/// Due / Overdue against the chosen From / To dates (clock = 2026-10-14, Dubai): the dates only choose WHICH instalments are read; Due (inside the current month) and Overdue (before it)
/// are always measured against today's month, a range can never read the next month, Both counts a unit once, and an explicit range is never widened or narrowed silently.
/// </summary>
public sealed class CampaignDateRangeTests
{
    private const string Stage = "CurrentMonthReminder";

    /// <summary>Unit 101: Aug 200, Sep 300, Oct 500, Nov 700 (not yet due). Unit 102: Oct 400. Unit 103: Aug 600.</summary>
    private static CollectionsCampaignAppServiceTests.Harness Seeded()
    {
        var h = new CollectionsCampaignAppServiceTests.Harness();
        h.Campaign.FinancialSourceValidated = true;
        PactRow(h, "3001", 101, new(2026, 8, 20), 200m); PactRow(h, "3001", 101, new(2026, 9, 5), 300m);
        PactRow(h, "3001", 101, new(2026, 10, 10), 500m); PactRow(h, "3001", 101, new(2026, 11, 5), 700m);
        PactRow(h, "3002", 102, new(2026, 10, 12), 400m); PactRow(h, "3003", 103, new(2026, 8, 3), 600m);
        return h;
    }

    private static void PactRow(CollectionsCampaignAppServiceTests.Harness h, string tenant, int unit, DateOnly due, decimal amount) =>
        h.Source.Items.Add(CollectionsCampaignAppServiceTests.Row(day: 1, amount: amount, tenant: tenant, unit: unit) with { DueDate = due.ToDateTime(TimeOnly.MinValue), VoucherNumber = $"V-{unit}-{due:yyyyMMdd}" });

    private static async Task<CollectionsCampaignPreviewDto> Preview(CollectionsCampaignAppServiceTests.Harness h, DateOnly? from, DateOnly? to, string status = "Both", DateOnly? businessDate = null) =>
        (await h.Service.PreviewAsync(h.Manager, Stage, businessDate, dateFrom: from, dateTo: to, minTotal: 0m, paymentStatus: status)).Value!;

    private static string Figures(CollectionsCampaignPreviewDto r) => string.Join(" ", r.Items.OrderBy(i => i.UnitCode).Select(i => $"{i.UnitCode}:{i.DueAmount:0}/{i.OverdueAmount:0}"));

    [Fact]
    public async Task APastRange_IsOverdue_NeverDue()
    {
        var h = Seeded();
        var both = await Preview(h, new(2026, 8, 1), new(2026, 8, 31));
        Assert.Equal("TP140-101:0/200 TP140-103:0/600", Figures(both));
        Assert.Empty((await Preview(h, new(2026, 8, 1), new(2026, 8, 31), "Due")).Items);                 // nothing in a past month is Due
        Assert.NotNull((await Preview(h, new(2026, 8, 1), new(2026, 8, 31), "Due")).PaymentStatusNote);   // and the page says why
    }

    [Fact]
    public async Task TheCurrentMonthRange_IsDue_AndTheDaysStillToComeAreIncluded()
    {
        var h = Seeded();
        var both = await Preview(h, new(2026, 10, 1), new(2026, 10, 31));
        Assert.Equal("TP140-101:500/0 TP140-102:400/0", Figures(both));                                    // Oct 10 and Oct 12 (Oct 12 is before 14, both are Due: the month decides, not the day)
        Assert.Empty((await Preview(h, new(2026, 10, 1), new(2026, 10, 31), "Overdue")).Items);
    }

    [Fact]
    public async Task ACrossMonthRange_SplitsIntoOverdueAndDue_AndCountsEachUnitOnce()
    {
        var h = Seeded();
        var both = await Preview(h, new(2026, 9, 15), new(2026, 10, 20));
        Assert.Equal("TP140-101:500/0 TP140-102:400/0", Figures(both));                                    // the 5 Sep instalment lies before 15 Sep: outside the chosen range, not silently included
        var wider = await Preview(h, new(2026, 9, 1), new(2026, 10, 31));
        Assert.Equal("TP140-101:500/300 TP140-102:400/0", Figures(wider));
        Assert.Equal(2, wider.TotalCount);
        Assert.Equal(wider.Items.Count, wider.Items.Select(i => (i.TenantId, i.UnitCode)).Distinct().Count());
        var due = await Preview(h, new(2026, 9, 1), new(2026, 10, 31), "Due");
        var overdue = await Preview(h, new(2026, 9, 1), new(2026, 10, 31), "Overdue");
        Assert.Equal(wider.Items.Sum(i => i.TotalAmount), due.Items.Sum(i => i.TotalAmount) + overdue.Items.Sum(i => i.TotalAmount));   // Both = Due + Overdue, no overlap, no gap
    }

    [Fact]
    public async Task ARangeThatReachesIntoFutureMonths_IsClampedToTheEndOfTheCurrentMonth_AndSaysSo()
    {
        var h = Seeded();
        var r = await Preview(h, new(2026, 10, 1), new(2026, 12, 31));
        Assert.Equal(new DateOnly(2026, 10, 31), r.DateTo);                                                // the November instalment (700) is not Due or Overdue and is not listed
        Assert.Equal("TP140-101:500/0 TP140-102:400/0", Figures(r));
    }

    [Fact]
    public async Task ARangeEntirelyInTheFuture_IsRefused_NotAnEmptyList()
    {
        var h = Seeded();
        var result = await h.Service.PreviewAsync(h.Manager, Stage, null, dateFrom: new(2026, 11, 1), dateTo: new(2026, 12, 31), minTotal: 0m);
        Assert.Equal(CollectionsOutcome.InvalidRequest, result.Outcome);
    }

    [Fact]
    public async Task DueAndOverdue_AreMeasuredAgainstToday_NotAgainstThePreviewDate()
    {
        var h = Seeded();
        // Previewing "as of" 14 September does not turn September into the current month: today is in October, so a September instalment is Overdue either way.
        var asSeptember = await Preview(h, new(2026, 9, 1), new(2026, 9, 30), businessDate: new(2026, 9, 14));
        Assert.Equal("TP140-101:0/300", Figures(asSeptember));
        Assert.Equal(Figures(await Preview(h, new(2026, 9, 1), new(2026, 9, 30))), Figures(asSeptember));
    }

    [Fact]
    public async Task MinimumTotal_UsesTheSelectedStatusAmountsOfTheChosenRange()
    {
        var h = Seeded();
        // In Sep-Oct unit 101 owes 500 Due + 300 Overdue = 800; unit 102 owes 400. Minimum 450 under Both keeps 101 only; under Overdue 101's 300 does not pass.
        var both = (await h.Service.PreviewAsync(h.Manager, Stage, null, dateFrom: new(2026, 9, 1), dateTo: new(2026, 10, 31), minTotal: 450m, paymentStatus: "Both")).Value!;
        var overdue = (await h.Service.PreviewAsync(h.Manager, Stage, null, dateFrom: new(2026, 9, 1), dateTo: new(2026, 10, 31), minTotal: 250m, paymentStatus: "Overdue")).Value!;
        var overdueHigher = (await h.Service.PreviewAsync(h.Manager, Stage, null, dateFrom: new(2026, 9, 1), dateTo: new(2026, 10, 31), minTotal: 450m, paymentStatus: "Overdue")).Value!;
        Assert.Equal(["TP140-101"], both.Items.Select(i => i.UnitCode));
        Assert.Equal(["TP140-101"], overdue.Items.Select(i => i.UnitCode));
        Assert.Empty(overdueHigher.Items);
    }
}

using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Services;

/// <summary>Stage + Project / Tower + Payment status + dates + Minimum Total together: one rule for the preview, the totals and both exports.</summary>
public sealed class CampaignCombinedFilterTests
{
    private const string Stage = "CurrentMonthReminder";

    /// <summary>
    /// Clock = 2026-10-14. Tower 7: unit 101 owes October 500 (Due) + September 300 (Overdue); unit 102 owes October 400 (Due); unit 103 owes September 600 only.
    /// Tower 8: unit 201 owes October 700 (Due).
    /// </summary>
    private static CollectionsCampaignAppServiceTests.Harness Seeded()
    {
        var h = new CollectionsCampaignAppServiceTests.Harness();
        h.Campaign.FinancialSourceValidated = true;
        PactReceivableInstalment Row(int day, decimal amount, string tenant, int unit, int tower, DateTime? due = null) =>
            CollectionsCampaignAppServiceTests.Row(day, amount, tenant, unit: unit) with { TowerId = tower, TowerNumber = tower == 7 ? "140" : "141", TowerName = tower == 7 ? "Al Ghaf" : "Al Sidr", DueDate = due ?? new(2026, 10, day) };
        h.Source.Items.Add(Row(10, 500m, "3001", 101, 7));
        h.Source.Items.Add(Row(5, 300m, "3001", 101, 7, new(2026, 9, 5)));
        h.Source.Items.Add(Row(12, 400m, "3002", 102, 7));
        h.Source.Items.Add(Row(3, 600m, "3003", 103, 7, new(2026, 9, 3)));
        h.Source.Items.Add(Row(9, 700m, "4001", 201, 8));
        return h;
    }

    private static string[] Units(CollectionsCampaignPreviewDto r) => r.Items.Select(i => i.UnitCode).Order().ToArray();

    [Fact]
    public async Task ProjectOnly_ListsThatTowersUnitsAndNothingElse()
    {
        var h = Seeded();
        var tower7 = (await h.Service.PreviewAsync(h.Manager, Stage, towerId: 7, minTotal: 0m)).Value!;
        var all = (await h.Service.PreviewAsync(h.Manager, Stage, minTotal: 0m)).Value!;
        Assert.Equal(["TP140-101", "TP140-102", "TP140-103"], Units(tower7));
        Assert.Equal(4, all.TotalCount);
        Assert.Equal(7, tower7.TowerId);
    }

    [Theory]
    [InlineData("Both", 7, new[] { "TP140-101", "TP140-102", "TP140-103" }, 900)]   // listed: all three; qualifying: stage amounts 500 + 400 (103 is not eligible for the current-month stage)
    [InlineData("Due", 7, new[] { "TP140-101", "TP140-102" }, 900)]
    [InlineData("Overdue", 7, new[] { "TP140-101", "TP140-103" }, 500)]   // 101 stays eligible (its stage amount is unchanged); 103 is listed but not eligible
    [InlineData("Due", 8, new[] { "TP140-201" }, 700)]
    [InlineData("Overdue", 8, new string[0], 0)]
    public async Task StageProjectAndPaymentStatus_Together_ListedUnitsAndQualifyingAmount(string status, int tower, string[] units, int stageAmount)
    {
        var h = Seeded();
        var report = (await h.Service.PreviewAsync(h.Manager, Stage, towerId: tower, minTotal: 0m, paymentStatus: status)).Value!;
        Assert.Equal(units.Order(), Units(report));
        Assert.Equal(units.Length, report.TotalCount);
        Assert.Equal(stageAmount, (int)(report.EligibleTotalAmount ?? 0m));
    }

    [Fact]
    public async Task AllFiltersCombined_Stage_Project_Status_Dates_MinimumTotal()
    {
        var h = Seeded();
        // Tower 7, Due only, instalments due 11-31 October, more than AED 450 in total: only unit 102 (its October 400 is below 450 -> out) ... so none; lower the floor and 102 appears.
        var strict = (await h.Service.PreviewAsync(h.Manager, Stage, towerId: 7, dateFrom: new(2026, 10, 11), dateTo: new(2026, 10, 31), minTotal: 450m, paymentStatus: "Due")).Value!;
        Assert.Empty(strict.Items);
        var relaxed = (await h.Service.PreviewAsync(h.Manager, Stage, towerId: 7, dateFrom: new(2026, 10, 11), dateTo: new(2026, 10, 31), minTotal: 350m, paymentStatus: "Due")).Value!;
        Assert.Equal(["TP140-102"], Units(relaxed));
        Assert.Equal(400m, relaxed.EligibleTotalAmount);
        Assert.Equal(400m, relaxed.EligibleOutstandingAmount);
        // The same dates with Overdue: nothing is overdue inside October, and the page says why.
        var overdue = (await h.Service.PreviewAsync(h.Manager, Stage, towerId: 7, dateFrom: new(2026, 10, 11), dateTo: new(2026, 10, 31), minTotal: 0m, paymentStatus: "Overdue")).Value!;
        Assert.Empty(overdue.Items);
        Assert.Contains("Overdue means due before 2026-10-01", overdue.PaymentStatusNote);
    }

    [Fact]
    public async Task TheQualifyingAmount_CoversTheWholeFilteredList_NotThePage()
    {
        var h = Seeded();
        var paged = (await h.Service.PreviewAsync(h.Manager, Stage, page: 2, pageSize: 1, minTotal: 0m, paymentStatus: "Due")).Value!;
        var whole = (await h.Service.PreviewAsync(h.Manager, Stage, page: 1, pageSize: 100, minTotal: 0m, paymentStatus: "Due")).Value!;
        Assert.Single(paged.Items);
        Assert.Equal(whole.EligibleTotalAmount, paged.EligibleTotalAmount);
        Assert.Equal(500m + 400m + 700m, whole.EligibleTotalAmount);
        // ... and equals the sum of the stage amounts the export file carries.
        var csv = (await h.Service.ExportAsync(h.Manager, Stage, "review", minTotal: 0m, paymentStatus: "Due")).Value!;
        Assert.Equal(3, csv.RowCount);
    }

    [Theory]
    [InlineData("Due", "2026-09-01", "2026-09-30", true)]       // dates end before the current month: nothing can be Due
    [InlineData("Due", "2026-10-01", "2026-10-31", false)]
    [InlineData("Overdue", "2026-10-01", "2026-10-31", true)]   // dates start in the current month: nothing can be Overdue
    [InlineData("Overdue", "2026-09-01", "2026-10-31", false)]
    [InlineData("Both", "2026-10-01", "2026-10-31", false)]
    public async Task TheNote_SaysWhenTheStatusCannotMatchTheDates(string status, string from, string to, bool noted)
    {
        var h = Seeded();
        var report = (await h.Service.PreviewAsync(h.Manager, Stage, dateFrom: DateOnly.Parse(from), dateTo: DateOnly.Parse(to), minTotal: 0m, paymentStatus: status)).Value!;
        Assert.Equal(noted, report.PaymentStatusNote is not null);
    }

    [Theory]
    [InlineData("Both", 7)]
    [InlineData("Due", 7)]
    [InlineData("Overdue", 7)]
    [InlineData("Due", 8)]
    public async Task BothExports_ContainExactlyThePreviewedUnits_ForTheSameProjectAndStatus(string status, int tower)
    {
        var h = Seeded();
        var preview = (await h.Service.PreviewAsync(h.Manager, Stage, towerId: tower, minTotal: 0m, paymentStatus: status)).Value!;
        var review = (await h.Service.ExportAsync(h.Manager, Stage, "review", towerId: tower, minTotal: 0m, paymentStatus: status)).Value!;
        Assert.Equal(preview.TotalCount, review.RowCount);
        foreach (var u in new[] { "TP140-101", "TP140-102", "TP140-103", "TP140-201" })
            Assert.Equal(preview.Items.Any(i => i.UnitCode == u), review.Csv.Contains(u, StringComparison.Ordinal));
    }
}

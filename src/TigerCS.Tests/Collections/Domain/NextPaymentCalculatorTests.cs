using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Domain;

/// <summary>Fixture tests of the pure selection rules. They say nothing about what EDSM's rows mean.</summary>
public class NextPaymentCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 10, 8);

    private static UnpaidInstalmentFact F(int days, decimal amount, long? unit = 1, int company = 4) =>
        new(company, 3001, unit, $"V{days}", Today.AddDays(days), amount);

    [Fact]
    public void PicksTheEarliestDateStrictlyAfterToday()
    {
        var result = NextPaymentCalculator.SelectNext([F(40, 5_000m), F(10, 7_500.25m), F(70, 5_000m)], Today)!;

        Assert.Equal(Today.AddDays(10), result.DueDate);
        Assert.Equal(7_500.25m, result.Amount);
    }

    [Fact]
    public void OverdueAndDueToday_AreExcluded_NeverTheNextPayment_ButCounted()
    {
        var result = NextPaymentCalculator.SelectNext([F(-30, 9_000m), F(-1, 100m), F(0, 2_000m), F(45, 3_000m)], Today)!;

        Assert.Equal(Today.AddDays(45), result.DueDate);
        Assert.Equal(3_000m, result.Amount);                  // the overdue 9,000 + 100 and today's 2,000 are not in it
        Assert.Equal(3, result.OnOrBeforeTodayExcluded);
    }

    [Fact]
    public void OnlyOverdue_MeansNoUpcomingInstalment()
    {
        Assert.Null(NextPaymentCalculator.SelectNext([F(-5, 100m), F(0, 100m)], Today));
        Assert.Null(NextPaymentCalculator.SelectNext([], Today));
    }

    [Fact]
    public void TwoUnitsDueOnTheSameDate_AreOnePaymentDateWithTwoLines_NeverASilentPick()
    {
        var result = NextPaymentCalculator.SelectNext([F(20, 1_000m, unit: 22), F(20, 2_500m, unit: 11), F(20, 400m, unit: null), F(90, 9m, unit: 5)], Today)!;

        Assert.Equal(3_900m, result.Amount);
        Assert.Equal([(11L, 2_500m), (22L, 1_000m), (null, 400m)], result.Units.Select(u => (u.UnitId, u.Amount)));
    }

    [Fact]
    public void InstalmentsWithNothingRemaining_AreIgnored()
    {
        var result = NextPaymentCalculator.SelectNext([F(5, 0m), F(6, -50m), F(30, 800m)], Today)!;

        Assert.Equal(Today.AddDays(30), result.DueDate);
    }

    [Fact]
    public void SameInputInAnyOrder_GivesTheSameAnswer()
    {
        var facts = new[] { F(20, 1m, 3), F(20, 2m, 1), F(8, 5m, 2), F(8, 6m, null) };

        var a = NextPaymentCalculator.SelectNext(facts, Today)!;
        var b = NextPaymentCalculator.SelectNext(facts.Reverse(), Today)!;

        Assert.Equal(a.DueDate, b.DueDate);
        Assert.Equal(a.Units, b.Units);
    }

    [Fact]
    public void AmountsAreRoundedOnce_AfterSumming()
    {
        var result = NextPaymentCalculator.SelectNext([F(7, 0.004m, 1), F(7, 0.004m, 2), F(7, 0.004m, 3)], Today)!;

        Assert.Equal(0.01m, result.Amount);      // 0.012 -> 0.01, not three 0.00s
    }
}

using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Domain;

/// <summary>The FAQ's reminder calendar and eligibility — window boundaries, month end, February, settled suppression.</summary>
public class ReminderPolicyTests
{
    private static readonly ReminderRuleSettings Defaults = new();

    private static FinancialAccountSnapshot Account(params FinancialInstalment[] instalments) =>
        new("ACC-1", "9001", null, null, null, "AED", DateTime.UtcNow, null, instalments,
            [new FinancialCharge("F", "Fine", null, 500m, 500m, null, true)], []);

    private static IReadOnlyList<ReminderType> Open(int year, int month, int day) =>
        ReminderPolicy.OpenWindows(new DateOnly(year, month, day), Defaults).Select(w => w.Type).ToList();

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(28, false)]
    public void OverdueWindow_IsDaysOneToFour(int day, bool open) =>
        Assert.Equal(open, Open(2026, 10, day).Contains(ReminderType.OverdueMoreThanOneMonth));

    [Theory]
    [InlineData(14, false)]
    [InlineData(15, true)]
    [InlineData(16, false)]
    public void CurrentMonthWindow_IsTheFifteenth(int day, bool open) =>
        Assert.Equal(open, Open(2026, 10, day).Contains(ReminderType.CurrentMonthDue));

    [Theory]
    [InlineData(2026, 10, 28, true)]   // 31-day month
    [InlineData(2026, 10, 27, false)]
    [InlineData(2026, 10, 29, false)]
    [InlineData(2026, 11, 27, true)]   // 30-day month
    [InlineData(2027, 2, 25, true)]    // February, common year
    [InlineData(2027, 2, 26, false)]
    [InlineData(2028, 2, 26, true)]    // February, leap year
    [InlineData(2028, 2, 25, false)]
    public void MonthEndWindow_IsThreeDaysBeforeTheLastDay(int year, int month, int day, bool open) =>
        Assert.Equal(open, Open(year, month, day).Contains(ReminderType.MonthEndFollowUp));

    [Fact]
    public void OverdueMoreThanOneMonth_CalendarMonthBoundary()
    {
        var today = new DateOnly(2026, 10, 3);

        // Due 3 Sep: exactly one month — not MORE than one month.
        var exactlyOneMonth = ReminderPolicy.Evaluate(Account(new FinancialInstalment("A", 1, new DateOnly(2026, 9, 3), 1_000m, 1_000m)),
            ReminderType.OverdueMoreThanOneMonth, today, Defaults);
        Assert.False(exactlyOneMonth.IsEligible);

        var overOneMonth = ReminderPolicy.Evaluate(Account(new FinancialInstalment("A", 1, new DateOnly(2026, 9, 2), 1_000m, 1_000m)),
            ReminderType.OverdueMoreThanOneMonth, today, Defaults);
        Assert.True(overOneMonth.IsEligible);
        Assert.Equal(1_000m, overOneMonth.Amount);
    }

    [Fact]
    public void OverdueCutoff_ClampsAtMonthEnd_AndHonoursFixedDays()
    {
        Assert.Equal(new DateOnly(2027, 2, 28), ReminderPolicy.OverdueCutoff(new DateOnly(2027, 3, 31), Defaults));
        Assert.Equal(new DateOnly(2028, 2, 29), ReminderPolicy.OverdueCutoff(new DateOnly(2028, 3, 31), Defaults));
        Assert.Equal(new DateOnly(2027, 3, 1), ReminderPolicy.OverdueCutoff(new DateOnly(2027, 3, 31), Defaults with { OverdueAgeRule = OverdueAgeRule.FixedDays }));
    }

    [Fact]
    public void OverdueReminder_StatesAllOverduePrincipal_NotOnlyTheOldPart()
    {
        var today = new DateOnly(2026, 10, 2);
        var result = ReminderPolicy.Evaluate(Account(
                new FinancialInstalment("A", 1, new DateOnly(2026, 8, 10), 1_000m, 400m),
                new FinancialInstalment("B", 2, new DateOnly(2026, 9, 10), 1_000m, 1_000m),
                new FinancialInstalment("C", 3, new DateOnly(2026, 10, 10), 1_000m, 1_000m)),
            ReminderType.OverdueMoreThanOneMonth, today, Defaults);

        Assert.True(result.IsEligible);
        Assert.Equal(1_400m, result.Amount);
    }

    [Fact]
    public void SettledCurrentMonth_IsSuppressed()
    {
        var result = ReminderPolicy.Evaluate(Account(new FinancialInstalment("A", 1, new DateOnly(2026, 10, 10), 1_000m, 0m)),
            ReminderType.CurrentMonthDue, new DateOnly(2026, 10, 15), Defaults);

        Assert.False(result.IsEligible);
        Assert.Equal(ReminderPolicy.SettledReason, result.Reason);
    }

    [Fact]
    public void FinesAloneNeverTriggerAReminder_AndAreIncludedOnlyWhenConfigured()
    {
        var today = new DateOnly(2026, 10, 15);
        var settledWithFine = ReminderPolicy.Evaluate(Account(new FinancialInstalment("A", 1, new DateOnly(2026, 10, 10), 1_000m, 0m)),
            ReminderType.CurrentMonthDue, today, Defaults with { IncludeFinesInReminderAmount = true });
        Assert.False(settledWithFine.IsEligible);

        var owing = Account(new FinancialInstalment("A", 1, new DateOnly(2026, 10, 20), 1_000m, 1_000m));
        Assert.Equal(1_000m, ReminderPolicy.Evaluate(owing, ReminderType.CurrentMonthDue, today, Defaults).Amount);
        Assert.Equal(1_500m, ReminderPolicy.Evaluate(owing, ReminderType.CurrentMonthDue, today, Defaults with { IncludeFinesInReminderAmount = true }).Amount);
    }

    [Fact]
    public void InconsistentSource_IsNeverReminded()
    {
        var account = Account(new FinancialInstalment("A", 1, new DateOnly(2026, 10, 10), 1_000m, 1_000m)) with { ReportedOutstandingPrincipal = 1m };
        var result = ReminderPolicy.Evaluate(account, ReminderType.Manual, new DateOnly(2026, 10, 15), Defaults);

        Assert.False(result.IsEligible);
        Assert.Equal(ReminderPolicy.InconsistentReason, result.Reason);
    }

    [Fact]
    public void CycleKeys_OncePerWindowIsMonthly_DailyAndManualAreDaily()
    {
        var day = new DateOnly(2026, 10, 2);
        Assert.Equal("2026-10", ReminderPolicy.CycleKey(ReminderType.OverdueMoreThanOneMonth, day, Defaults));
        Assert.Equal("2026-10-02", ReminderPolicy.CycleKey(ReminderType.OverdueMoreThanOneMonth, day, Defaults with { SendFrequency = ReminderSendFrequency.Daily }));
        Assert.Equal("2026-10-02", ReminderPolicy.CycleKey(ReminderType.Manual, day, Defaults));
    }
}

using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Domain;

/// <summary>The FAQ calendar and eligibility: window boundaries, calendar-month age, month end, February, leap years, settled suppression.</summary>
public class ReminderPolicyTests
{
    private static readonly ReminderRuleSettings Draft = new();

    private static FinancialAccountSnapshot Account(params FinancialInstalment[] instalments) =>
        new("ACC-1", 12345, null, null, null, "AED", DateTime.UtcNow, null, instalments,
            [new FinancialCharge("P", FinancialChargeType.Penalty, 500m, 500m, null, true)], []);

    private static IReadOnlyList<ReminderType> Open(int year, int month, int day) =>
        ReminderPolicy.OpenWindows(new DateOnly(year, month, day), Draft).Select(w => w.Type).ToList();

    [Theory]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(31, false)]
    public void OverdueMonthly_Opens1stTo4th(int day, bool open) =>
        Assert.Equal(open, Open(2026, 10, day).Contains(ReminderType.OverdueMonthly));

    [Theory]
    [InlineData(14, false)]
    [InlineData(15, true)]
    [InlineData(16, false)]
    public void CurrentMonth_OpensOnThe15th(int day, bool open) =>
        Assert.Equal(open, Open(2026, 10, day).Contains(ReminderType.CurrentMonth));

    [Theory]
    [InlineData(2026, 10, 28, true)]
    [InlineData(2026, 10, 27, false)]
    [InlineData(2026, 10, 29, false)]
    [InlineData(2026, 11, 27, true)]
    [InlineData(2027, 2, 25, true)]
    [InlineData(2027, 2, 26, false)]
    [InlineData(2028, 2, 26, true)]
    [InlineData(2028, 2, 25, false)]
    public void MonthEndFollowUp_OpensThreeDaysBeforeMonthEnd(int year, int month, int day, bool open) =>
        Assert.Equal(open, Open(year, month, day).Contains(ReminderType.MonthEndFollowUp));

    [Fact]
    public void ReproducesTheSpecificationsCandidateExample()
    {
        // §5: 2 October, calendar month → cutoff 2 September; the four
        // instalments before it qualify, AED 30,000; October's does not.
        var result = ReminderPolicy.Evaluate(Account(
                new("INST-JUN-2026", new DateOnly(2026, 6, 1), 5_000m, 5_000m),
                new("INST-JUL-2026", new DateOnly(2026, 7, 1), 5_000m, 5_000m),
                new("INST-AUG-2026", new DateOnly(2026, 8, 1), 10_000m, 10_000m),
                new("INST-SEP-2026", new DateOnly(2026, 9, 1), 10_000m, 10_000m),
                new("INST-OCT-2026", new DateOnly(2026, 10, 15), 10_000m, 10_000m)),
            ReminderType.OverdueMonthly, new DateOnly(2026, 10, 2), Draft);

        Assert.True(result.IsEligible);
        Assert.Equal(30_000m, result.Amount);
        Assert.Equal("UnpaidPrincipalOlderThanOneCalendarMonth", result.AmountBasis);
        Assert.Equal(["INST-JUN-2026", "INST-JUL-2026", "INST-AUG-2026", "INST-SEP-2026"], result.InstalmentIds);
        Assert.Equal(new DateOnly(2026, 6, 1), result.OldestUnpaidDueDate);
        Assert.Equal("2026-10:OverdueMonthly", ReminderPolicy.CycleKey(ReminderType.OverdueMonthly, new DateOnly(2026, 10, 2), Draft));
    }

    [Fact]
    public void OneCalendarMonth_IsStrict_AtTheBoundary()
    {
        var businessDate = new DateOnly(2026, 10, 2);
        var exactlyOneMonth = ReminderPolicy.Evaluate(Account(new FinancialInstalment("A", new DateOnly(2026, 9, 2), 1_000m, 1_000m)),
            ReminderType.OverdueMonthly, businessDate, Draft);
        var olderByADay = ReminderPolicy.Evaluate(Account(new FinancialInstalment("A", new DateOnly(2026, 9, 1), 1_000m, 1_000m)),
            ReminderType.OverdueMonthly, businessDate, Draft);

        Assert.False(exactlyOneMonth.IsEligible);
        Assert.Equal(ReminderPolicy.NothingOldEnoughReason, exactlyOneMonth.Reason);
        Assert.True(olderByADay.IsEligible);
    }

    [Fact]
    public void Cutoff_ClampsAtMonthEnd_InLeapAndCommonYears_AndHonoursFixedDays()
    {
        Assert.Equal(new DateOnly(2027, 2, 28), ReminderPolicy.OverdueCutoff(new DateOnly(2027, 3, 31), Draft));
        Assert.Equal(new DateOnly(2028, 2, 29), ReminderPolicy.OverdueCutoff(new DateOnly(2028, 3, 31), Draft));
        Assert.Equal(new DateOnly(2027, 3, 1), ReminderPolicy.OverdueCutoff(new DateOnly(2027, 3, 31), Draft with { OverdueAgeRule = OverdueAgeRule.FixedDays }));
    }

    [Fact]
    public void PartialSettlement_QuotesOnlyWhatRemains()
    {
        var result = ReminderPolicy.Evaluate(Account(
                new FinancialInstalment("A", new DateOnly(2026, 8, 10), 10_000m, 2_500m),
                new FinancialInstalment("B", new DateOnly(2026, 8, 20), 10_000m, 0m)),
            ReminderType.OverdueMonthly, new DateOnly(2026, 10, 2), Draft);

        Assert.Equal(2_500m, result.Amount);
        Assert.Equal(["A"], result.InstalmentIds);
    }

    [Fact]
    public void CurrentMonth_QuotesThisMonthsUnpaidPrincipal_AndASettledMonthIsSuppressed()
    {
        var owing = ReminderPolicy.Evaluate(Account(
                new FinancialInstalment("A", new DateOnly(2026, 10, 10), 10_000m, 6_000m),
                new FinancialInstalment("B", new DateOnly(2026, 11, 10), 10_000m, 10_000m)),
            ReminderType.CurrentMonth, new DateOnly(2026, 10, 15), Draft);
        Assert.Equal(6_000m, owing.Amount);
        Assert.Equal("CurrentMonthUnpaidPrincipal", owing.AmountBasis);

        var settled = ReminderPolicy.Evaluate(Account(new FinancialInstalment("A", new DateOnly(2026, 10, 10), 10_000m, 0m)),
            ReminderType.MonthEndFollowUp, new DateOnly(2026, 10, 28), Draft);
        Assert.False(settled.IsEligible);
        Assert.Equal(ReminderPolicy.SettledReason, settled.Reason);
    }

    [Fact]
    public void PenaltiesAloneNeverTrigger_AndAreAddedOnlyWhenConfigured()
    {
        var owing = Account(new FinancialInstalment("A", new DateOnly(2026, 10, 20), 1_000m, 1_000m));
        Assert.Equal(1_000m, ReminderPolicy.Evaluate(owing, ReminderType.CurrentMonth, new DateOnly(2026, 10, 15), Draft).Amount);
        var withFines = ReminderPolicy.Evaluate(owing, ReminderType.CurrentMonth, new DateOnly(2026, 10, 15), Draft with { IncludePenaltiesAndFees = true });
        Assert.Equal(1_500m, withFines.Amount);
        Assert.EndsWith("PlusPayablePenaltiesAndFees", withFines.AmountBasis, StringComparison.Ordinal);

        var onlyPenalty = ReminderPolicy.Evaluate(Account(new FinancialInstalment("A", new DateOnly(2026, 10, 10), 1_000m, 0m)),
            ReminderType.CurrentMonth, new DateOnly(2026, 10, 15), Draft with { IncludePenaltiesAndFees = true });
        Assert.False(onlyPenalty.IsEligible);
    }

    [Fact]
    public void AnInconsistentSource_IsNeverReminded()
    {
        var account = Account(new FinancialInstalment("A", new DateOnly(2026, 8, 10), 1_000m, 1_000m)) with { ReportedOutstandingPrincipal = 1m };
        var result = ReminderPolicy.Evaluate(account, ReminderType.OverdueMonthly, new DateOnly(2026, 10, 2), Draft);

        Assert.False(result.IsEligible);
        Assert.Equal(ReminderPolicy.InconsistentReason, result.Reason);
    }

    [Fact]
    public void CycleKeys_MonthlyByDefault_DailyWhenConfigured() =>
        Assert.Equal("2026-10-02:OverdueMonthly",
            ReminderPolicy.CycleKey(ReminderType.OverdueMonthly, new DateOnly(2026, 10, 2), Draft with { SendFrequency = ReminderSendFrequency.Daily }));

    [Theory]
    [InlineData(ReminderChannel.VoiceBot, ChannelStatus.Answered, true)]
    [InlineData(ReminderChannel.VoiceBot, ChannelStatus.NoAnswer, true)]
    [InlineData(ReminderChannel.VoiceBot, ChannelStatus.Delivered, false)]
    [InlineData(ReminderChannel.Sms, ChannelStatus.Delivered, true)]
    [InlineData(ReminderChannel.Email, ChannelStatus.Answered, false)]
    public void DeliveryStatuses_AreValidatedPerChannel(ReminderChannel channel, ChannelStatus status, bool valid) =>
        Assert.Equal(valid, ReminderPolicy.IsValidDeliveryStatus(channel, status));

    [Fact]
    public void UnreportedCharges_BlockOnlyAReminderWhoseAmountIncludesThem()
    {
        var account = new FinancialAccountSnapshot("ACC-1", 12345, null, null, null, "AED", DateTime.UtcNow, null,
            [new FinancialInstalment("I1", new DateOnly(2026, 8, 10), 1_000m, 1_000m)], Charges: null, Payments: []);
        var day = new DateOnly(2026, 10, 2);

        var principalOnly = ReminderPolicy.Evaluate(account, ReminderType.OverdueMonthly, day, Draft);
        var withCharges = ReminderPolicy.Evaluate(account, ReminderType.OverdueMonthly, day, new ReminderRuleSettings { IncludePenaltiesAndFees = true });

        Assert.True(principalOnly.IsEligible);
        Assert.Equal(1_000m, principalOnly.Amount);
        Assert.False(withCharges.IsEligible);
        Assert.Equal(ReminderPolicy.ChargesNotReportedReason, withCharges.Reason);
    }
}

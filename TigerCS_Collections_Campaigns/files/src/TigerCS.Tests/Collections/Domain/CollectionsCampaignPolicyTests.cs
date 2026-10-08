using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Domain;

public sealed class CollectionsCampaignPolicyTests
{
    [Theory]
    [InlineData(CollectionsCampaignStage.OverdueReminder, 1)]
    [InlineData(CollectionsCampaignStage.CurrentMonthReminder, 14)]
    [InlineData(CollectionsCampaignStage.FollowUpReminder, 28)]
    [InlineData(CollectionsCampaignStage.LegalReferral, 30)]
    public void ScheduleUsesTheCommunicationTable(CollectionsCampaignStage stage, int day) =>
        Assert.Equal(new DateOnly(2026, 10, day), Assert.Single(CollectionsCampaignPolicy.ScheduledDates(stage, new(2026, 10, 8))));

    [Theory]
    [InlineData(2026, 28)]
    [InlineData(2028, 29)]
    public void ShortMonthReferralFallsOnLastDay_AndFollowUpStaysOn28(int year, int last)
    {
        Assert.Equal(new DateOnly(year, 2, last), Assert.Single(CollectionsCampaignPolicy.ScheduledDates(CollectionsCampaignStage.LegalReferral, new(year, 2, 1))));
        Assert.Equal(new DateOnly(year, 2, 28), Assert.Single(CollectionsCampaignPolicy.ScheduledDates(CollectionsCampaignStage.FollowUpReminder, new(year, 2, 1))));
        Assert.Equal([new DateOnly(year, 2, 12), new DateOnly(year, 2, 14)], CollectionsCampaignPolicy.ScheduledDates(CollectionsCampaignStage.LegalNotice, new(year, 2, 1)));
    }

    [Fact]
    public void OverdueIsStrictlyOlderThanOneCalendarMonth_AndQuotesRemainingPrincipal()
    {
        var result = CollectionsCampaignPolicy.Evaluate([
            new(new(2026, 9, 13), 125m), new(new(2026, 9, 14), 1000m), new(new(2026, 8, 10), 0m)
        ], CollectionsCampaignStage.OverdueReminder, new(2026, 10, 14));
        Assert.Equal(125m, result.Amount);
        Assert.Equal(new DateOnly(2026, 9, 13), result.EarliestDueDate);
    }

    [Theory]
    [InlineData(CollectionsCampaignStage.CurrentMonthReminder)]
    [InlineData(CollectionsCampaignStage.FollowUpReminder)]
    public void CurrentMonthIncludesUpcomingPayments_ExcludesOtherMonthsAndSettledRows(CollectionsCampaignStage stage)
    {
        var result = CollectionsCampaignPolicy.Evaluate([
            new(new(2026, 9, 30), 5000m), new(new(2026, 10, 1), 120m), new(new(2026, 10, 31), 80m),
            new(new(2026, 11, 1), 6000m), new(new(2026, 10, 5), 0m)
        ], stage, new(2026, 10, 14));
        Assert.Equal(200m, result.Amount);
    }

    [Theory]
    [InlineData(1500, "BelowThreshold")]
    [InlineData(1501, "Qualifies")]
    public void LegalNoticeUsesPreviousMonthOnly_AndStrictThreshold(int amount, string expected)
    {
        var result = CollectionsCampaignPolicy.Evaluate([
            new(new(2026, 9, 15), amount), new(new(2026, 8, 15), 90000m), new(new(2026, 10, 15), 90000m)
        ], CollectionsCampaignStage.LegalNotice, new(2026, 10, 14));
        Assert.Equal(amount, result.Amount);
        Assert.Equal(expected, result.Reason);
    }

    [Theory]
    [InlineData(20000, "BelowThreshold")]
    [InlineData(20001, "Qualifies")]
    public void ReferralThresholdAppliesToTheBalanceStrictlyOlderThanThreeMonths(int amount, string expected)
    {
        var result = CollectionsCampaignPolicy.Evaluate([
            new(new(2026, 7, 13), amount), new(new(2026, 7, 14), 90000m), new(new(2026, 8, 1), 90000m)
        ], CollectionsCampaignStage.LegalReferral, new(2026, 10, 14));
        Assert.Equal(amount, result.Amount);
        Assert.Equal(expected, result.Reason);
    }

    [Fact]
    public void SameDateRowsHaveNoCampaignAmount()
    {
        var result = CollectionsCampaignPolicy.Evaluate([new(new(2026, 10, 1), 100m), new(new(2026, 10, 1), 100m)],
            CollectionsCampaignStage.CurrentMonthReminder, new(2026, 10, 14));
        Assert.Null(result.Amount);
        Assert.Equal("AmbiguousInstalments", result.Reason);
    }
}

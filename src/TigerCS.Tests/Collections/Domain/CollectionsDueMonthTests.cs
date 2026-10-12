using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Domain;

/// <summary>
/// The single Due / Overdue rule (Asia/Dubai calendar month of today):
/// Overdue = DueDate &lt; monthStart; Due = DueDate &gt;= monthStart AND DueDate &lt; nextMonthStart; later is out of scope.
/// </summary>
public sealed class CollectionsDueMonthTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Theory]
    [InlineData(2026, 9, 30, CollectionsDueClass.Overdue)]       // the day before the month starts
    [InlineData(2026, 10, 1, CollectionsDueClass.Due)]           // first day of the month
    [InlineData(2026, 10, 12, CollectionsDueClass.Due)]          // today
    [InlineData(2026, 10, 31, CollectionsDueClass.Due)]          // last day of the month, still to come
    [InlineData(2026, 11, 1, CollectionsDueClass.NotInScope)]    // first day of next month
    [InlineData(2025, 3, 10, CollectionsDueClass.Overdue)]       // a previous year
    [InlineData(2027, 1, 1, CollectionsDueClass.NotInScope)]
    public void OnTheTwelfthOfOctober2026(int y, int m, int d, CollectionsDueClass expected)
    {
        var today = D(2026, 10, 12);
        Assert.Equal(expected, CollectionsDueMonth.Classify(D(y, m, d), today));
        Assert.Equal(expected == CollectionsDueClass.Overdue, CollectionsDueMonth.IsOverdue(D(y, m, d), today));
        Assert.Equal(expected == CollectionsDueClass.Due, CollectionsDueMonth.IsDue(D(y, m, d), today));
        Assert.Equal(expected != CollectionsDueClass.NotInScope, CollectionsDueMonth.IsInScope(D(y, m, d), today));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(31)]
    public void TheDayOfTheMonthNeverChangesTheClassification(int dayOfToday)
    {
        var today = D(2026, 10, dayOfToday);
        Assert.Equal(CollectionsDueClass.Overdue, CollectionsDueMonth.Classify(D(2026, 9, 30), today));
        foreach (var day in new[] { 1, 12, 31 }) Assert.Equal(CollectionsDueClass.Due, CollectionsDueMonth.Classify(D(2026, 10, day), today));
        Assert.Equal(CollectionsDueClass.NotInScope, CollectionsDueMonth.Classify(D(2026, 11, 1), today));
    }

    [Fact]
    public void DecemberRollsIntoJanuary_AsTheNextMonthStart()
    {
        var today = D(2026, 12, 15);
        Assert.Equal(D(2026, 12, 1), CollectionsDueMonth.MonthStart(today));
        Assert.Equal(D(2027, 1, 1), CollectionsDueMonth.NextMonthStart(today));          // not "month 13"
        Assert.Equal(D(2026, 12, 31), CollectionsDueMonth.MonthEnd(today));
        Assert.Equal(CollectionsDueClass.Overdue, CollectionsDueMonth.Classify(D(2026, 11, 30), today));
        Assert.Equal(CollectionsDueClass.Due, CollectionsDueMonth.Classify(D(2026, 12, 1), today));
        Assert.Equal(CollectionsDueClass.Due, CollectionsDueMonth.Classify(D(2026, 12, 31), today));
        Assert.Equal(CollectionsDueClass.NotInScope, CollectionsDueMonth.Classify(D(2027, 1, 1), today));
    }

    [Fact]
    public void JanuaryLooksBackAtDecemberOfThePreviousYear()
    {
        var today = D(2027, 1, 10);
        Assert.Equal(D(2027, 1, 1), CollectionsDueMonth.MonthStart(today));
        Assert.Equal(CollectionsDueClass.Overdue, CollectionsDueMonth.Classify(D(2026, 12, 31), today));     // the last day of the old year
        Assert.Equal(CollectionsDueClass.Due, CollectionsDueMonth.Classify(D(2027, 1, 1), today));
        Assert.Equal(CollectionsDueClass.Due, CollectionsDueMonth.Classify(D(2027, 1, 31), today));
        Assert.Equal(CollectionsDueClass.NotInScope, CollectionsDueMonth.Classify(D(2027, 2, 1), today));
    }

    [Theory]
    [InlineData(2026, 2, 28)]   // non-leap February
    [InlineData(2028, 2, 29)]   // leap February
    [InlineData(2026, 4, 30)]
    public void MonthEndHandlesShortMonths(int y, int m, int lastDay)
    {
        var today = D(y, m, 3);
        Assert.Equal(D(y, m, lastDay), CollectionsDueMonth.MonthEnd(today));
        Assert.Equal(CollectionsDueClass.Due, CollectionsDueMonth.Classify(D(y, m, lastDay), today));
        Assert.Equal(CollectionsDueClass.NotInScope, CollectionsDueMonth.Classify(D(y, m, lastDay).AddDays(1), today));
    }

    [Fact]
    public void ClampToNeverReachesTheNextMonth_AndKeepsAnEarlierEnd()
    {
        var today = D(2026, 10, 12);
        Assert.Equal(D(2026, 10, 31), CollectionsDueMonth.ClampTo(D(2099, 12, 31), today));
        Assert.Equal(D(2026, 10, 31), CollectionsDueMonth.ClampTo(D(2026, 10, 31), today));
        Assert.Equal(D(2026, 9, 30), CollectionsDueMonth.ClampTo(D(2026, 9, 30), today));
        Assert.Equal(D(2026, 12, 31), CollectionsDueMonth.ClampTo(D(2027, 6, 1), D(2026, 12, 20)));
    }

    [Theory]
    [InlineData(CollectionsCampaignStage.OverdueReminder)]
    [InlineData(CollectionsCampaignStage.CurrentMonthReminder)]
    [InlineData(CollectionsCampaignStage.FollowUpReminder)]
    [InlineData(CollectionsCampaignStage.LegalNotice)]
    [InlineData(CollectionsCampaignStage.LegalReferral)]
    public void TheDefaultCampaignWindowEndsWithThePreviewMonth_ForEveryStage(CollectionsCampaignStage stage)
    {
        Assert.Equal(D(2026, 10, 31), CollectionsCampaignPolicy.DefaultDateTo(stage, D(2026, 10, 12)));
        Assert.Equal(D(2026, 12, 31), CollectionsCampaignPolicy.DefaultDateTo(stage, D(2026, 12, 1)));
        Assert.Equal(D(2027, 1, 31), CollectionsCampaignPolicy.DefaultDateTo(stage, D(2027, 1, 31)));
    }

    [Fact]
    public void TheStageEligibilityRangesAreUnchanged()
    {
        var date = D(2026, 10, 12);
        Assert.Equal((null, D(2026, 9, 12)), CollectionsCampaignPolicy.StageRange(CollectionsCampaignStage.OverdueReminder, date));
        Assert.Equal((D(2026, 9, 1), D(2026, 10, 1)), CollectionsCampaignPolicy.StageRange(CollectionsCampaignStage.LegalNotice, date));
        Assert.Equal((D(2026, 10, 1), D(2026, 11, 1)), CollectionsCampaignPolicy.StageRange(CollectionsCampaignStage.CurrentMonthReminder, date));
        Assert.Equal((D(2026, 10, 1), D(2026, 11, 1)), CollectionsCampaignPolicy.StageRange(CollectionsCampaignStage.FollowUpReminder, date));
        Assert.Equal((null, D(2026, 7, 12)), CollectionsCampaignPolicy.StageRange(CollectionsCampaignStage.LegalReferral, date));
    }
}

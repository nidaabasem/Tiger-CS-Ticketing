namespace TigerCS.Domain.Modules.Collections;

public enum CollectionsCampaignStage
{
    OverdueReminder = 1,
    CurrentMonthReminder = 2,
    FollowUpReminder = 3,
    LegalNotice = 4,
    LegalReferral = 5
}

public sealed record CampaignInstalment(DateOnly DueDate, decimal RemainingAmount);
public sealed record CampaignAmount(decimal? Amount, DateOnly? EarliestDueDate, string Reason);

/// <summary>The 7 October 2026 communication table, with thresholds evaluated per unit.
/// Values are the source's remaining principal, never the original plan or the customer's total balance.</summary>
public static class CollectionsCampaignPolicy
{
    public static IReadOnlyList<DateOnly> ScheduledDates(CollectionsCampaignStage stage, DateOnly businessDate)
    {
        var last = DateTime.DaysInMonth(businessDate.Year, businessDate.Month);
        DateOnly Day(int day) => new(businessDate.Year, businessDate.Month, Math.Min(day, last));
        return stage switch
        {
            CollectionsCampaignStage.OverdueReminder => [Day(1)],
            CollectionsCampaignStage.CurrentMonthReminder => [Day(14)],
            CollectionsCampaignStage.FollowUpReminder => [Day(28)],
            CollectionsCampaignStage.LegalNotice => [Day(12), Day(14)],
            CollectionsCampaignStage.LegalReferral => [Day(30)],
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };
    }

    public static CampaignAmount Evaluate(IEnumerable<CampaignInstalment> instalments,
        CollectionsCampaignStage stage, DateOnly businessDate)
    {
        if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        var monthStart = new DateOnly(businessDate.Year, businessDate.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var previousMonthStart = monthStart.AddMonths(-1);
        var rows = instalments.Where(i => i.RemainingAmount > 0 && (stage switch
        {
            CollectionsCampaignStage.OverdueReminder => i.DueDate < businessDate.AddMonths(-1),
            CollectionsCampaignStage.LegalNotice => i.DueDate >= previousMonthStart && i.DueDate < monthStart,
            CollectionsCampaignStage.LegalReferral => i.DueDate < businessDate.AddMonths(-3),
            _ => i.DueDate >= monthStart && i.DueDate < monthEnd
        })).ToList();
        if (rows.Count == 0) return new(0m, null, "NoQualifyingBalance");
        var earliest = rows.Min(i => i.DueDate);
        // The existing source cannot distinguish valid same-date instalments from duplicated allocations.
        if (rows.GroupBy(i => i.DueDate).Any(g => g.Count() > 1))
            return new(null, earliest, "AmbiguousInstalments");
        var amount = rows.Sum(i => i.RemainingAmount);
        var qualifies = stage switch
        {
            CollectionsCampaignStage.LegalNotice => amount > 1500m,
            CollectionsCampaignStage.LegalReferral => amount > 20000m,
            _ => amount > 0m
        };
        return new(amount, earliest, qualifies ? "Qualifies" : "BelowThreshold");
    }

    /// <summary>
    /// Flags (without changing eligibility) where the due-date window excludes instalments the confirmed stage rule would
    /// otherwise qualify. The window is an additional reviewer filter; the communication policy above is unchanged.
    /// </summary>
    public static IReadOnlyList<string> RangeNotes(CollectionsCampaignStage stage, DateOnly businessDate, DateOnly from, DateOnly to)
    {
        var monthStart = new DateOnly(businessDate.Year, businessDate.Month, 1);
        static string F(DateOnly d) => d.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        var notes = new List<string>();
        switch (stage)
        {
            case CollectionsCampaignStage.OverdueReminder:
            case CollectionsCampaignStage.LegalReferral:
                var cutoff = stage == CollectionsCampaignStage.OverdueReminder ? businessDate.AddMonths(-1) : businessDate.AddMonths(-3);
                notes.Add($"Stage rule: instalments due before {F(cutoff)}. Instalments due before {F(from)} are excluded by the From date.");
                if (to >= cutoff) notes.Add($"Instalments due on or after {F(cutoff)} do not qualify for this stage even though they are inside the date range.");
                break;
            case CollectionsCampaignStage.LegalNotice:
                if (from > monthStart.AddMonths(-1) || to < monthStart.AddDays(-1))
                    notes.Add("The date range does not cover the whole previous calendar month that this stage uses.");
                break;
            default:
                if (from > monthStart || to < monthStart.AddMonths(1).AddDays(-1))
                    notes.Add($"This stage includes the whole preview month ({F(monthStart)} to {F(monthStart.AddMonths(1).AddDays(-1))}, including upcoming dates); the date range excludes part of it.");
                break;
        }
        return notes;
    }
}

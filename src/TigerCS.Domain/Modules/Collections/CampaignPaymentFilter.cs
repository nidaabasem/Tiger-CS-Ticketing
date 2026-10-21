namespace TigerCS.Domain.Modules.Collections;

/// <summary>
/// The Campaigns "Payment status" filter. Due = instalments due inside the current Dubai month, Overdue = due before it (<see cref="CollectionsDueMonth"/>).
/// Both (the default) lists a unit when it has either. A single status keeps only that status' instalments in the unit's Due / Overdue / Total, so the Minimum Total,
/// the counts, the qualifying amount, the pages and both CSV exports all agree. It never changes the stage eligibility rule.
/// </summary>
public enum CampaignPaymentFilter
{
    Both = 0,
    Due = 1,
    Overdue = 2
}

public static class CampaignPaymentFilters
{
    /// <summary>Blank, "Both" and "All" mean Both; anything unknown is refused (false).</summary>
    public static bool TryParse(string? value, out CampaignPaymentFilter filter)
    {
        filter = CampaignPaymentFilter.Both;
        if (string.IsNullOrWhiteSpace(value)) return true;
        switch (value.Trim().ToLowerInvariant())
        {
            case "both" or "all": return true;
            case "due": filter = CampaignPaymentFilter.Due; return true;
            case "overdue": filter = CampaignPaymentFilter.Overdue; return true;
            default: return false;
        }
    }

    /// <summary>The query-string / display value ("Both", "Due", "Overdue").</summary>
    public static string Name(CampaignPaymentFilter filter) => filter.ToString();
}

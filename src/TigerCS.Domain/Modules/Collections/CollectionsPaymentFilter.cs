namespace TigerCS.Domain.Modules.Collections;

/// <summary>
/// Which instalments the Receivables list shows by PAYMENT status (separate from the date-based Due/Overdue classification).
/// Outstanding = remaining balance &gt; 0 (unpaid + partially paid + rows whose status the source cannot tell apart).
/// </summary>
public enum CollectionsPaymentFilter { Outstanding = 0, Unpaid = 1, PartiallyPaid = 2, FullyPaid = 3, All = 4 }

public static class CollectionsPaymentFilters
{
    public static string ToWire(CollectionsPaymentFilter filter) => filter switch
    {
        CollectionsPaymentFilter.Outstanding => "outstanding", CollectionsPaymentFilter.Unpaid => "unpaid",
        CollectionsPaymentFilter.PartiallyPaid => "partial", CollectionsPaymentFilter.FullyPaid => "paid", _ => "all"
    };

    public static bool TryParse(string? value, out CollectionsPaymentFilter filter)
    {
        filter = CollectionsPaymentFilter.Outstanding;
        switch ((value ?? "outstanding").Trim().ToLowerInvariant())
        {
            case "" or "outstanding": return true;
            case "unpaid": filter = CollectionsPaymentFilter.Unpaid; return true;
            case "partial" or "partiallypaid": filter = CollectionsPaymentFilter.PartiallyPaid; return true;
            case "paid" or "fullypaid": filter = CollectionsPaymentFilter.FullyPaid; return true;
            case "all": filter = CollectionsPaymentFilter.All; return true;
            default: return false;
        }
    }

    /// <summary>
    /// The minimum-outstanding filter compares the REMAINING balance, which is 0 for a paid row; for Fully paid and All it is therefore switched off
    /// (it would silently hide the very rows those views exist to show).
    /// </summary>
    public static bool MinimumApplies(CollectionsPaymentFilter filter) => filter is not (CollectionsPaymentFilter.FullyPaid or CollectionsPaymentFilter.All);

    /// <summary>Payment status of one instalment. Mirrors the SQL publish step; "Unknown" means the source cannot tell and nothing is guessed.</summary>
    public static string Classify(decimal remaining, decimal? original, decimal? allocated, string? sourceStatus, bool breakdownPresent)
    {
        if (breakdownPresent)
        {
            if (original is null || allocated is null || original < 0 || allocated < 0 || Math.Abs(original.Value - allocated.Value - remaining) > 0.01m) return "Unknown";
            return remaining == 0 ? "FullyPaid" : allocated > 0 ? "PartiallyPaid" : "Unpaid";
        }
        return remaining == 0 && string.Equals(sourceStatus?.Trim(), "Paid", StringComparison.OrdinalIgnoreCase) ? "FullyPaid" : "Unknown";
    }
}

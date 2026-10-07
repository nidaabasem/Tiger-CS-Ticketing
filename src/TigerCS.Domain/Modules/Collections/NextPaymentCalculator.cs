namespace TigerCS.Domain.Modules.Collections;

/// <summary>
/// One unpaid instalment, <b>already established as unpaid</b> by the caller from
/// EDSM rows whose meaning the EDSM owners have confirmed (see
/// <c>CollectionsNextPaymentOptions</c>). This type carries no interpretation of
/// its own: <paramref name="RemainingAmount"/> is what is still unpaid, never the
/// original scheduled amount.
/// </summary>
public sealed record UnpaidInstalmentFact(
    int CompanyId, long TenantId, long? UnitId, string Reference, DateOnly DueDate, decimal RemainingAmount);

/// <summary>The unpaid instalments falling due on the earliest upcoming date, grouped by unit (a null unit id is one group).</summary>
public sealed record NextPaymentSelection(
    DateOnly DueDate, decimal Amount, IReadOnlyList<NextPaymentUnitLine> Units, int OnOrBeforeTodayExcluded);

public sealed record NextPaymentUnitLine(long? UnitId, decimal Amount, int InstalmentCount);

/// <summary>
/// Deterministic selection of the next <i>upcoming</i> unpaid instalment for one
/// (company, tenant).
///
/// <list type="bullet">
/// <item><description><b>Upcoming means strictly after the business date.</b> An instalment due
/// today or earlier is due/overdue, which EDSM's payment-summary already reports in
/// <c>dueAmount</c> (<c>ChequeDueDate ≤ today</c> for owned companies, contract §3.4). It is
/// excluded here and only counted in <see cref="NextPaymentSelection.OnOrBeforeTodayExcluded"/>,
/// so overdue money is never presented as, or added to, the next payment.</description></item>
/// <item><description><b>Earliest date wins; nothing else is chosen between.</b> All unpaid
/// instalments on that date are summed (two units due the same day are one payment date with two
/// lines, not a silent pick of one).</description></item>
/// <item><description>Instalments with no remaining amount (≤ 0) are ignored; amounts are
/// rounded to 2 dp once, after summing. Units are ordered by unit id (null last) so output is stable.</description></item>
/// </list>
/// </summary>
public static class NextPaymentCalculator
{
    /// <returns>The selection, or null when no unpaid instalment falls after <paramref name="businessDate"/>.</returns>
    public static NextPaymentSelection? SelectNext(IEnumerable<UnpaidInstalmentFact> facts, DateOnly businessDate)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var live = facts.Where(f => f.RemainingAmount > 0m).ToList();
        var excluded = live.Count(f => f.DueDate <= businessDate);
        var upcoming = live.Where(f => f.DueDate > businessDate).ToList();
        if (upcoming.Count == 0)
        {
            return null;
        }

        var date = upcoming.Min(f => f.DueDate);
        var onDate = upcoming.Where(f => f.DueDate == date).ToList();
        var lines = onDate
            .GroupBy(f => f.UnitId)
            .OrderBy(g => g.Key is null ? 1 : 0)
            .ThenBy(g => g.Key)
            .Select(g => new NextPaymentUnitLine(g.Key, Math.Round(g.Sum(f => f.RemainingAmount), 2), g.Count()))
            .ToList();

        return new NextPaymentSelection(date, Math.Round(onDate.Sum(f => f.RemainingAmount), 2), lines, excluded);
    }
}

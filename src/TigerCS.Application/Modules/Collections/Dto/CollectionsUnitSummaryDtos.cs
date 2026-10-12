namespace TigerCS.Application.Modules.Collections.Dto;

/// <summary>The customer of a unit as linked from CRM and PACT. Every value carries where it came from (<c>Crm</c>, <c>Pact</c> or empty when nobody has it).</summary>
public sealed record CollectionsUnitPartyDto(string Name, string Mobile, string Email, string NameSource, string MobileSource, string EmailSource);

/// <summary>One unpaid or partly paid instalment of the unit due before the end of the current Dubai month (Overdue: before its first day; Due: inside it, any day). Original / paid are null when PACT did not return them (never estimated).</summary>
public sealed record CollectionsUnitInstalmentDto(string Description, DateOnly DueDate, decimal? OriginalAmount, decimal? PaidAmount, decimal RemainingAmount, string Status, int? DaysLate);

/// <summary>One payment of the unit's schedule. <c>Status</c>: <c>Overdue</c> (due before the first day of the current Dubai month), <c>Due</c> (due in the current Dubai month, any day) or <c>NotYetDue</c> (after it). Scheduled / paid are null when the source did not return them (never estimated).</summary>
public sealed record CollectionsUnitScheduleLineDto(string Description, DateOnly DueDate, decimal? ScheduledAmount, decimal? PaidAmount, decimal RemainingAmount, string Status, int? DaysLate);

/// <summary>
/// The Payment tab's figures of ONE unit. Everything is derived from <see cref="Schedule"/> (every unpaid instalment of the unit, future ones included), so the cards and the table
/// can never disagree: <see cref="Overdue"/> = lines due before the first day of the current Dubai month, <see cref="DueThisMonth"/> = lines due inside that month (any day),
/// <see cref="RemainingBalance"/> = every line (future ones too; it is NOT Due + Overdue). The month rule is <c>CollectionsDueMonth</c>, the one Receivables and Campaigns use.
/// <see cref="TotalAmount"/> and <see cref="PaidAmount"/> stay null ("Unavailable"): the PACT receivables source returns the remaining amount of unpaid instalments only (see
/// docs/Collections/Unit-Payment-Summary.md), and nothing is inferred from it.
/// </summary>
public sealed record CollectionsUnitPaymentFiguresDto(
    DateOnly MonthStart, DateOnly MonthEnd, IReadOnlyList<CollectionsUnitScheduleLineDto> Schedule, DateTime? LastUpdatedUtc, decimal? TotalAmount = null, decimal? PaidAmount = null)
{
    public decimal RemainingBalance => Schedule.Sum(l => l.RemainingAmount);

    public decimal DueThisMonth => Schedule.Where(l => l.Status == "Due").Sum(l => l.RemainingAmount);

    public decimal Overdue => Schedule.Where(l => l.Status == "Overdue").Sum(l => l.RemainingAmount);
}

/// <summary>
/// Payment Summary of ONE unit, found by its tower + unit code (never by a phone number). <c>LinkStatus</c>: <c>Linked</c> (one customer, one PACT account),
/// <c>NeedsReview</c> (see <c>ReviewReasons</c>: several CRM customers, CRM and PACT contacts disagree, several PACT accounts) or <c>NotFound</c> (neither CRM nor PACT
/// knows the unit). <c>FinancialStatus</c>: <c>Available</c> (amounts shown), <c>NoDues</c> (PACT confirms nothing is due), <c>NoFinancialData</c> (PACT holds nothing
/// usable for the unit - NOT the same as zero: the amounts are null) or <c>Withheld</c> (the PACT account is not unambiguous). <c>FinancialReason</c> says why when there are no amounts: <c>PactHoldsNoRecord</c>, <c>CompanySnapshotNotLoaded</c> or <c>SeveralPactAccounts</c>. Due / Overdue / Total are null unless
/// <c>Available</c> or <c>NoDues</c>; Total = Due + Overdue.
/// </summary>
public sealed record CollectionsUnitPaymentSummaryDto(
    string UnitKey, string UnitCode, int? CompanyId, string? TowerNumber, string? TowerName,
    string LinkStatus, IReadOnlyList<string> ReviewReasons,
    CollectionsUnitPartyDto Customer, string CrmStatus, int? CrmCustomerId, int? CrmUnitId, string? PactTenantId, int? PactUnitId,
    string FinancialStatus, string? FinancialDetail, decimal? Due, decimal? Overdue, decimal? Total,
    IReadOnlyList<CollectionsUnitInstalmentDto> Instalments, DateOnly AsOf, DateTime? PactReadAtUtc, string PactFreshness, string? FinancialReason = null,
    CollectionsUnitPaymentFiguresDto? Payments = null);

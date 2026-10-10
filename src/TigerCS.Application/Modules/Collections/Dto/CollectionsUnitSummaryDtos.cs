namespace TigerCS.Application.Modules.Collections.Dto;

/// <summary>The customer of a unit as linked from CRM and PACT. Every value carries where it came from (<c>Crm</c>, <c>Pact</c> or empty when nobody has it).</summary>
public sealed record CollectionsUnitPartyDto(string Name, string Mobile, string Email, string NameSource, string MobileSource, string EmailSource);

/// <summary>One unpaid instalment of the unit due today or earlier (Dubai). Original / paid are null when PACT did not return them (never estimated).</summary>
public sealed record CollectionsUnitInstalmentDto(string Description, DateOnly DueDate, decimal? OriginalAmount, decimal? PaidAmount, decimal RemainingAmount, string Status, int? DaysLate);

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
    IReadOnlyList<CollectionsUnitInstalmentDto> Instalments, DateOnly AsOf, DateTime? PactReadAtUtc, string PactFreshness, string? FinancialReason = null);

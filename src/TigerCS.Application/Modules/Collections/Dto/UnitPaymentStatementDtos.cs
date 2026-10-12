namespace TigerCS.Application.Modules.Collections.Dto;

/// <summary>
/// One figure of the payment statement as the source stated it. <c>Status</c>: <c>Provided</c> (Value is the source's number), <c>ZeroOrLess</c> (EDSM states late fines only when above
/// zero, so an empty value of an OWNED company is documented to mean "none"; Value is 0), <c>NotComputed</c> (rented companies: the source never computes fines), or any
/// <c>EdsmAmountStatus</c> name (<c>Missing</c>, <c>Empty</c>, <c>Unreadable</c>, <c>FormatNotConfigured</c>): Value is null and the screen says "Unavailable" - never zero.
/// </summary>
public sealed record StatementAmountDto(string Status, decimal? Value, string? Raw);

/// <summary>One payment row exactly as the source returned it (partial payments / splits are separate rows and are never merged). <c>Status</c>: <c>Paid</c>, <c>Due</c> or <c>Outstanding</c> (the list the row came from).</summary>
public sealed record StatementRowDto(DateOnly? Date, string? DateRaw, string Status, decimal? Amount, string? AmountRaw, string? ChequeNumber, string? PaymentType);

/// <summary>
/// The complete payment statement of ONE unit: Due | Outstanding | Paid | Fines | Total, then Payment Details (Date | Status | Amount | Cheque Number) with paid, unpaid and future rows.
/// Source: PACT's EDSM payment summary / transactions of the unit's (company, tenant) account - the same source as the CRM "Pact Payment" screen. EDSM aggregates per account, so the
/// statement is shown ONLY when PACT's contract list proves that the account belongs exclusively to this unit; otherwise <c>Status</c> says why and no amount is shown.
/// <c>Status</c>: <c>Available</c>; <c>AmountsUnreadable</c> (EDSM answered but no figure could be read: number format not configured); <c>NotExclusive</c> (the account also covers
/// <c>OtherUnits</c>); <c>UnitNotFound</c> (PACT lists no active contract for this unit under the customer's phone numbers); <c>Ambiguous</c> (several accounts claim the unit);
/// <c>IdentityMismatch</c>; <c>ContractEnded</c>; <c>Unverified</c> (PACT could not be fully read, so exclusivity cannot be proven); <c>CompanyNotSupported</c>; <c>NotConfigured</c>;
/// <c>SourceUnavailable</c>; <c>UnitNotResolved</c> (no PACT unit code yet). Nothing here is ever derived from the Due / Overdue collections snapshot.
/// </summary>
public sealed record UnitPaymentStatementDto(
    string Status, string? Detail, string UnitKey, int? CompanyId, string? CompanyName, string? BusinessModel, string? TenantId,
    IReadOnlyList<string> OtherUnits,
    StatementAmountDto? Due, StatementAmountDto? Outstanding, StatementAmountDto? Paid, StatementAmountDto? Fines, StatementAmountDto? Total,
    string OutstandingLabel, IReadOnlyList<StatementRowDto> Rows, bool RowsComplete, IReadOnlyList<string> Notes, string Currency, DateTime? RetrievedAtUtc, string? Source)
{
    public bool IsAvailable => Status == "Available";

    public static UnitPaymentStatementDto Withheld(string status, string? detail, string unitKey, int? companyId = null, string? tenantId = null, IReadOnlyList<string>? otherUnits = null) =>
        new(status, detail, unitKey, companyId, null, null, tenantId, otherUnits ?? [], null, null, null, null, null, "Outstanding", [], false, [], "AED", null, null);
}

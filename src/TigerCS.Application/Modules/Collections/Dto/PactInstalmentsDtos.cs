namespace TigerCS.Application.Modules.Collections.Dto;

/// <summary>
/// One instalment. Original and paid amounts are what the source returned; they are null (never estimated) when it did not.
/// PaymentStatus (Unpaid / PartiallyPaid / FullyPaid / Unknown) is separate from Classification (Overdue / Due / NotYetDue / NotApplicable),
/// so an instalment can be partially paid and overdue at once.
/// </summary>
public sealed record PactInstalmentRowDto(
    int CompanyId, string CompanyName, string? TowerNumber, string? TowerName, int? UnitId, string UnitCode, string TenantId,
    string CustomerName, string Mobile, string Email, string VoucherNumber, string ChequeNumber, DateOnly DueDate,
    decimal? OriginalAmount, decimal? PaidAmount, decimal RemainingAmount, string PaymentStatus, string Classification, string SourceStatus);

public sealed record PactInstalmentTotalsDto(
    int Count, decimal RemainingTotal, int OverdueCount, decimal OverdueRemaining, int DueCount, decimal DueRemaining,
    int NotYetDueCount, decimal NotYetDueRemaining, int FullyPaidCount);

/// <summary>Which payment views the loaded data can answer reliably (the UI disables the others and says why).</summary>
public sealed record PaymentViewAvailabilityDto(bool Outstanding, bool Unpaid, bool PartiallyPaid, bool FullyPaid, bool All, int UnclassifiedRows);

public sealed record PactInstalmentsPageDto(
    DateOnly BusinessDate, DateOnly AsOfMonthStart, DateOnly AsOfMonthEnd, DateOnly DateFrom, DateOnly DateTo, int? TowerId,
    string PaymentFilter, decimal MinAmount, bool MinAmountApplied, string Currency,
    PactInstalmentTotalsDto Totals, int Page, int PageSize, IReadOnlyList<PactInstalmentRowDto> Items,
    SnapshotStatusDto Snapshot, PaymentViewAvailabilityDto Views, IReadOnlyList<string> Notes, DateTime ReadAtUtc, ServerTimingsDto? Timings = null);

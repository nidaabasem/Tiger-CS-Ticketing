namespace TigerCS.Application.Modules.Collections.Dto;

public sealed record PactReceivableInstalmentDto(
    int? UnitId, string UnitCode, string ProjectCode, string VoucherNumber,
    string ChequeNumber, DateOnly DueDate, decimal RemainingAmount,
    string ReceivablesType, string PaymentStatus, string DueTiming, string SourceStatus);

public sealed record PactReceivableCustomerDto(
    int CompanyId, string CompanyName, string TenantId, string FullName,
    string Mobile, string Email, int? UnitId, string UnitCode, string ProjectCode,
    bool HasDue, bool HasOverdue,
    decimal? DueAmount, decimal? OverdueAmount, decimal? TotalAmount, string AmountStatus,
    DateOnly EarliestDueDate, int OverdueDays,
    IReadOnlyList<PactReceivableInstalmentDto> Instalments);

public sealed record PactReceivableCustomersDto(
    DateOnly BusinessDate, int ReportYear, int ReportMonth, DateOnly PeriodStart, DateOnly PeriodEnd, DateTime ReadAtUtc, bool LegacyExclusionsApplied,
    IReadOnlyList<int> CompanyIds, string Currency, string CurrencySource,
    int TotalCount, int DueCustomerCount, int OverdueCustomerCount,
    int Page, int PageSize, IReadOnlyList<PactReceivableCustomerDto> Items);

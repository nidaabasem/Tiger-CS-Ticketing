namespace TigerCS.Application.Modules.Collections.Dto;

public sealed record CollectionsCampaignContactDto(
    string RecordId, string CustomerKey, int CompanyId, string TenantId,
    string CustomerName, string Phone, string Email, int? UnitId, string UnitCode,
    string ProjectCode, decimal? Amount, string Currency, DateOnly? DueDate,
    string Stage, string CycleKey, string Status, string Reason,
    string? TowerNumber = null, string? TowerName = null,
    decimal DueAmount = 0m, decimal OverdueAmount = 0m, int? CrmCustomerId = null)
{
    /// <summary>Due + Overdue of the unit (instalments due today or earlier that are still unpaid).</summary>
    public decimal TotalAmount => DueAmount + OverdueAmount;
    public bool VoiceEligible => Status == "Ready" && Phone.Length > 0;
    public bool SmsEligible => Status == "Ready" && Phone.Length > 0;
    public bool EmailEligible => Status == "Ready" && Email.Length > 0;
}

public sealed record CollectionsCampaignPreviewDto(
    DateOnly BusinessDate, DateOnly LiveBusinessDate, DateTime ReadAtUtc,
    string Source, string Stage, string CycleKey, IReadOnlyList<DateOnly> ScheduledDates,
    bool IsScheduledDate, bool FinancialSourceValidated, bool LegacyExclusionsApplied,
    bool CanExportReview, int TotalCount, int ReadyCount, int ReviewCount, int Page, int PageSize,
    IReadOnlyList<CollectionsCampaignContactDto> Items,
    DateOnly? DateFrom = null, DateOnly? DateTo = null, IReadOnlyList<string>? RangeNotes = null,
    int? TowerId = null, SnapshotStatusDto? Snapshot = null, decimal MinAmount = 0m, ServerTimingsDto? Timings = null);

/// <summary>Internal API transport; the Web serves Csv as a UTF-8 downloadable file.</summary>
public sealed record CollectionsCampaignExportDto(string FileName, string Csv, int RowCount);

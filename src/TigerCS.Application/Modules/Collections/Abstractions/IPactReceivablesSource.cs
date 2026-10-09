using TigerCS.Application.Modules.Collections.Dto;

namespace TigerCS.Application.Modules.Collections.Abstractions;

public interface IPactReceivablesSource
{
    /// <summary>Reads instalments due on or before the end of <paramref name="throughDate"/> (the reporting month's last day).</summary>
    Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken);

    /// <summary>
    /// Reads only the requested instalment due-date window and, when <see cref="PactReceivablesRequest.CompanyId"/>
    /// is set, only that company's procedure. The default ignores the window (legacy sources); the SQL source honours it.
    /// </summary>
    Task<PactReceivablesSnapshot> ReadAsync(PactReceivablesRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request.ThroughDate, cancellationToken);
}

/// <summary>Due-date window (inclusive) and optional company scope pushed down to the PACT procedures.</summary>
/// <param name="FromDate">Passed as @StartDate; null uses the configured default start.</param>
/// <param name="ThroughDate">Last due date included.</param>
/// <param name="CompanyId">4 or 32 to read a single company; null reads both.</param>
/// <param name="TowerId">Local <c>CollectionsTowers.TowerId</c>; the company is resolved from the tower. Null = all towers.</param>
/// <param name="Class">Which instalments the source returns; <c>Any</c> applies only the window.</param>
/// <param name="AsOfDate">Picks the Due/Overdue classification month (first day of its month); defaults to the through date's month.</param>
/// <param name="MinAmount">An instalment is returned only when its remaining unpaid Amount is &gt;= this value (inclusive); 0 returns every positive balance.</param>
public sealed record PactReceivablesRequest(DateOnly? FromDate, DateOnly ThroughDate, int? CompanyId = null,
    int? TowerId = null, PactReceivableClass Class = PactReceivableClass.Any, DateOnly? AsOfDate = null, decimal MinAmount = 0m);

/// <summary>Source-side Due/Overdue filter. Overdue = due before the as-of month; Due = due within the as-of month (incl. its later days);
/// anything after the month end is neither and is never classified Overdue.</summary>
public enum PactReceivableClass { Any, DueOrOverdue, Due, Overdue }

public sealed record PactReceivableInstalment(
    int CompanyId, string TenantId, string FullName, string Mobile, string Email,
    int? UnitId, string UnitCode, string ProjectCode, string VoucherNumber,
    string ChequeNumber, DateTime DueDate, decimal Amount, string SourceStatus,
    string? TowerNumber = null, int? TowerId = null, string? TowerName = null,
    decimal? PlanAmount = null, bool AmountIsFloatingPoint = false, decimal? AllocatedAmount = null);

public sealed record PactReceivablesSnapshot(
    IReadOnlyList<PactReceivableInstalment> Items,
    DateTime ReadAtUtc,
    bool LegacyExclusionsApplied,
    SnapshotStatusDto? Snapshot = null);

public sealed class PactReceivablesSourceException(string message) : Exception(message);

/// <summary>The tower dropdown source (local table, no PACT access).</summary>
public interface ICollectionsTowerCatalog
{
    Task<IReadOnlyList<CollectionsTowerDto>> ListActiveAsync(CancellationToken cancellationToken);
}

/// <summary>Raised when the request names a tower that does not exist (or clashes with the company filter).</summary>
public sealed class PactReceivablesScopeException(string message) : Exception(message);

/// <summary>Runs one snapshot refresh (PACT -> staging -> validated publish). Overlap-safe; companies succeed or fail independently.</summary>
public interface IReceivablesRefresher
{
    Task<ReceivablesRefreshResult> RefreshAsync(string triggerSource, int? companyId, CancellationToken cancellationToken,
        DateOnly? fromDate = null, DateOnly? throughDate = null);
}

public sealed record ReceivablesRefreshCompanyResult(
    int CompanyId, string Status, int? RawRows, int? PublishedRows, int? ExcludedZeroRows,
    int? ExcludedInvalidUnitRows, int? ExcludedInvalidIdentityRows, int? ErrorNumber, string? ErrorMessage,
    int? FetchMs = null, int? ValidateMs = null, int? PublishMs = null);

/// <remarks>Status is Succeeded, PartialFailure, Failed or AlreadyRunning.</remarks>
public sealed record ReceivablesRefreshResult(Guid? RunId, string Status, string? Message, IReadOnlyList<ReceivablesRefreshCompanyResult> Companies);

/// <summary>
/// Starts a BACKGROUND load of a due-date range that the snapshot does not cover yet (the refresh procedure extends, never shrinks,
/// each company's coverage). Returns immediately; the pages show progress through <c>SnapshotStatusDto.LoadInProgress</c>.
/// </summary>
public interface IReceivablesRangeLoader
{
    Task<bool> EnqueueAsync(DateOnly from, DateOnly through, CancellationToken cancellationToken);
}

/// <summary>
/// A source that can answer the Receivables page by itself: filtering, per-apartment aggregation, counts and pagination happen in the
/// data store, so only one page of apartments (and their instalments) ever reaches the application.
/// </summary>
public interface IPactReceivablesPageSource
{
    Task<PactReceivablesPage> ReadPageAsync(PactReceivablesPageRequest request, CancellationToken cancellationToken);
}

/// <remarks>AsOf is the first day of the classification month (Overdue = due before it; Due = inside that month). Status is all, due or overdue
/// (apartments having a Due / an Overdue instalment). PhoneDigits are the digits of a phone-like search term, else null.</remarks>
public sealed record PactReceivablesPageRequest(
    DateOnly From, DateOnly To, DateOnly AsOf, decimal MinAmount, string Status, string? Search, string? PhoneDigits,
    int Page, int PageSize, int? CompanyId, int? TowerId);

/// <summary>One apartment of a page. Due/Overdue amount is null when two instalments of the bucket share a due date (needs review).</summary>
public sealed record PactReceivableApartment(
    int CompanyId, string TenantId, int? UnitId, string UnitCode, string FullName, string Mobile, string Email, string ProjectCode,
    string? TowerNumber, string? TowerName, int DueRows, int OverdueRows, decimal? DueAmount, decimal? OverdueAmount, DateOnly Earliest,
    IReadOnlyList<PactReceivableInstalment> Instalments);

public sealed record PactReceivablesPage(
    int TotalApartments, int DueApartments, int OverdueApartments, IReadOnlyList<PactReceivableApartment> Apartments,
    DateTime ReadAtUtc, SnapshotStatusDto Snapshot, double SqlMs);

/// <summary>Instalment-level reads (Receivables page): filtering by payment status, month/range, minimum amount, totals and paging all happen in the data store.</summary>
public interface IPactInstalmentSource
{
    Task<PactInstalmentsPage> ReadInstalmentsAsync(PactInstalmentsRequest request, CancellationToken cancellationToken);
}

/// <remarks>AsOf is the first day of the classification month. PaymentFilter is outstanding, unpaid, partial, paid or all.</remarks>
public sealed record PactInstalmentsRequest(
    DateOnly From, DateOnly To, DateOnly AsOf, decimal MinAmount, string PaymentFilter, string? Search, string? PhoneDigits,
    int Page, int PageSize, int? CompanyId, int? TowerId);

public sealed record PactInstalmentsPage(
    PactInstalmentTotalsDto Totals, IReadOnlyList<PactInstalmentRowDto> Rows, bool Unavailable, SnapshotStatusDto Snapshot, DateTime ReadAtUtc, double SqlMs);

/// <summary>
/// A source that evaluates the Campaigns preview/export itself: instalment filtering, per-unit aggregation, the stage amount rule, the unit-level review
/// flags, search, totals and paging all happen in the data store, so only the requested page of units (or, for an export, the bounded full result) reaches
/// the application. A source may answer <see cref="PactCampaignPage.Supported"/> = false (data not prepared for it); the caller then evaluates in memory.
/// </summary>
public interface IPactCampaignSource
{
    Task<PactCampaignPage> ReadCampaignAsync(PactCampaignRequest request, CancellationToken cancellationToken);
}

/// <remarks>
/// The unit's rows are those inside [From, To] (due DAY) with remaining Amount &gt; 0 and &gt;= MinAmount. Stage rows are the ones whose due day is in
/// [StageFrom, StageToExclusive) (StageFrom null = unbounded); a stage amount qualifies when its sum is &gt; Threshold. ContactRequired is false for the
/// internal legal-referral stage. PhoneDigits are the digits of a phone-like search term, else null. Offset/Take page the ordered units
/// (company, tenant, unit code, unit id).
/// </remarks>
public sealed record PactCampaignRequest(
    DateOnly From, DateOnly To, decimal MinAmount, DateOnly? StageFrom, DateOnly StageToExclusive, decimal Threshold, bool ContactRequired,
    string? Search, string? PhoneDigits, int Offset, int Take, int? CompanyId, int? TowerId);

/// <summary>Unit-level facts of one campaign candidate. <c>Flags</c> is a <see cref="TigerCS.Domain.Modules.Collections.CollectionsCampaignFlags"/> mask;
/// <c>Amount</c> is null when the unit's stage instalments are ambiguous.</summary>
public sealed record CampaignUnitFacts(
    int CompanyId, string TenantId, string FullName, string Phone, string Email, int? UnitId, string UnitCode, string ProjectCode,
    decimal? Amount, DateOnly? EarliestDue, int Flags, string? TowerNumber, string? TowerName);

/// <remarks>
/// Supported = false: the data store could not use its campaign engine for the snapshot (the caller evaluates in memory). BadIdentityRows: rows without
/// a valid company / customer identity (the campaign refuses to run). Total / Clean / Review count the candidate units over the whole filtered set,
/// before paging (Clean = no unit-level review flag; Review = at least one).
/// </remarks>
public sealed record PactCampaignPage(
    bool Supported, int BadIdentityRows, int Total, int Clean, int Review, IReadOnlyList<CampaignUnitFacts> Units,
    DateTime ReadAtUtc, SnapshotStatusDto Snapshot, double SqlMs);

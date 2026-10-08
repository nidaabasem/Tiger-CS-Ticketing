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
public sealed record PactReceivablesRequest(DateOnly? FromDate, DateOnly ThroughDate, int? CompanyId = null,
    int? TowerId = null, PactReceivableClass Class = PactReceivableClass.Any, DateOnly? AsOfDate = null);

/// <summary>Source-side Due/Overdue filter. Overdue = due before the as-of month; Due = due within the as-of month (incl. its later days);
/// anything after the month end is neither and is never classified Overdue.</summary>
public enum PactReceivableClass { Any, DueOrOverdue, Due, Overdue }

public sealed record PactReceivableInstalment(
    int CompanyId, string TenantId, string FullName, string Mobile, string Email,
    int? UnitId, string UnitCode, string ProjectCode, string VoucherNumber,
    string ChequeNumber, DateTime DueDate, decimal Amount, string SourceStatus,
    string? TowerNumber = null, int? TowerId = null, string? TowerName = null);

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
    int? ExcludedInvalidUnitRows, int? ExcludedInvalidIdentityRows, int? ErrorNumber, string? ErrorMessage);

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

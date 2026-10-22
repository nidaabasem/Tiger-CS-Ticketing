namespace TigerCS.Application.Modules.Collections.Dto;

/// <summary>An entry of the tower dropdown (dbo.CollectionsTowers). TowerId is a LOCAL identity, not a CRM project id.</summary>
public sealed record CollectionsTowerDto(int TowerId, string TowerNumber, string TowerName, int CompanyId, bool IsActive)
{
    public string Label => $"{TowerNumber} - {TowerName}";
}

/// <summary>State of one company's local snapshot. Refresh outcome and coverage are tracked per company.</summary>
/// <remarks>Freshness is <c>Fresh</c>, <c>Stale</c> (older than the allowed age) or <c>Missing</c> (never loaded: NOT an empty result).</remarks>
public sealed record SnapshotCompanyStatusDto(
    int CompanyId, string CompanyName, bool HasSnapshot, DateTime? LastSuccessUtc, DateTime? LastAttemptUtc,
    string LastAttemptStatus, int? LastErrorNumber, int ConsecutiveFailures, int SnapshotRowCount,
    DateOnly? CoverageFrom, DateOnly? CoverageThrough,
    int ExcludedInvalidUnitRows, decimal ExcludedInvalidUnitAmount,
    int ExcludedInvalidIdentityRows, decimal ExcludedInvalidIdentityAmount,
    int ContradictoryStatusRows, int UnknownStatusRows, string Freshness, int? AgeMinutes, bool RefreshInProgress = false, DateTime? RefreshStartedUtc = null, string? RunCompanyStatus = null,
    bool PaidRetained = false, bool BreakdownAvailable = false, int UnclassifiedRows = 0,
    DateTime? LastFinishedUtc = null, string? LastErrorDetail = null, int? LastFetchMs = null, int? LastRawRows = null, bool RefreshOverdue = false)
{
    /// <summary>The newest refresh attempt failed or was abandoned (timeout, cancellation, restart); the previous snapshot (if any) is still being served.</summary>
    public bool LastRefreshFailed => LastAttemptStatus is "Failed" or "Abandoned";

    /// <summary>
    /// What the page must say about this company. Exactly one of <see cref="SnapshotLoadStates"/>; <c>Loading</c> only while a refresh of THIS company really executes
    /// (and has not outlived the longest legitimate run), so a finished, failed or abandoned run is never reported as loading.
    /// </summary>
    public string State => RefreshInProgress ? (RefreshOverdue ? SnapshotLoadStates.Stuck : SnapshotLoadStates.Loading)
        : LastRefreshFailed ? SnapshotLoadStates.Failed
        : !HasSnapshot ? SnapshotLoadStates.NeverLoaded
        : Freshness == "Fresh" ? SnapshotLoadStates.Ready : SnapshotLoadStates.Stale;

    /// <summary>True while the page should keep checking: a refresh of this company is executing and within its expected duration. Every other state is final until a person or a schedule acts.</summary>
    public bool IsLoading => State == SnapshotLoadStates.Loading;

    /// <summary>Data is available to show (the last valid snapshot), whatever the state of the newest attempt.</summary>
    public bool HasData => HasSnapshot && LastSuccessUtc is not null;

    /// <summary>A safe, human sentence for the newest failure (null when the last attempt did not fail). Never contains server names, addresses or SQL text.</summary>
    public string? FailureText => LastRefreshFailed ? TigerCS.Application.Modules.Collections.Services.SnapshotFailureText.Describe(LastErrorNumber, LastErrorDetail, LastFetchMs, LastRawRows) : null;
}

/// <summary>The per-company load states a page can show.</summary>
public static class SnapshotLoadStates
{
    public const string Ready = "Ready", Loading = "Loading", Failed = "Failed", Stale = "Stale", NeverLoaded = "NeverLoaded", Stuck = "Stuck";
}

/// <summary>Receivables with no active tower row: reported, never dropped, never given an invented tower name.</summary>
public sealed record UnmatchedTowerDto(int CompanyId, string? TowerNumber, string Reason, long RowCount, decimal Amount);

/// <summary>A part of the requested due-date range that the company's snapshot does not hold (it must be loaded, it is NOT zero receivables).</summary>
public sealed record CoverageGapDto(int CompanyId, string CompanyName, DateOnly From, DateOnly Through);

public sealed record SnapshotStatusDto(
    IReadOnlyList<SnapshotCompanyStatusDto> Companies, IReadOnlyList<UnmatchedTowerDto> UnmatchedTowers, int MaxAgeMinutes,
    DateOnly? RequestedFrom = null, DateOnly? RequestedThrough = null, IReadOnlyList<CoverageGapDto>? CoverageGaps = null,
    bool? ScheduledRefreshActive = null, string? RefreshSchedule = null)
{
    public IReadOnlyList<CoverageGapDto> Gaps => CoverageGaps ?? [];
    /// <summary>The whole requested From/To range is inside the loaded coverage of every loaded company in scope.</summary>
    public bool RangeCovered => Gaps.Count == 0;
    public bool LoadInProgress => Companies.Any(c => c.IsLoading);
    /// <summary>Every loaded company in scope keeps fully paid instalments: "Fully paid" and "All" may be offered. False before the first load.</summary>
    public bool PaidRetained => Companies.Any(c => c.HasSnapshot) && Companies.Where(c => c.HasSnapshot).All(c => c.PaidRetained);
    /// <summary>Every loaded company in scope returned original + paid amounts: "Unpaid" and "Partially paid" can be told apart reliably.</summary>
    public bool BreakdownAvailable => Companies.Any(c => c.HasSnapshot) && Companies.Where(c => c.HasSnapshot).All(c => c.BreakdownAvailable);
    public int UnclassifiedRows => Companies.Sum(c => c.UnclassifiedRows);
    public bool NothingLoaded => Companies.Count > 0 && Companies.All(c => !c.HasSnapshot);
    /// <summary>Export / "this list is complete" requirement: fresh data AND the whole requested range covered.</summary>
    public bool IsReady => IsFresh && RangeCovered;
    public string? ReadyProblem => !IsFresh ? FreshnessProblem
        : !RangeCovered ? $"The selected dates are not fully loaded ({string.Join("; ", Gaps.Select(g => $"{g.CompanyName}: {g.From:dd MMM yyyy} to {g.Through:dd MMM yyyy}"))}). Load the missing data first."
        : null;
    /// <summary>Oldest successful refresh among the companies in scope (null when one was never loaded).</summary>
    public DateTime? OldestSuccessUtc => Companies.Any(c => !c.HasSnapshot) ? null : Companies.Min(c => c.LastSuccessUtc);
    public bool IsComplete => Companies.Count > 0 && Companies.All(c => c.HasSnapshot);
    public bool HasFailures => Companies.Any(c => c.LastRefreshFailed);
    /// <summary>The documented freshness requirement for exports: every company in scope loaded and no older than <see cref="MaxAgeMinutes"/>.</summary>
    public bool IsFresh => IsComplete && Companies.All(c => c.Freshness == "Fresh");
    public string? FreshnessProblem => Companies.FirstOrDefault(c => !c.HasSnapshot) is { } missing
        ? $"{missing.CompanyName} has not been loaded yet."
        : Companies.FirstOrDefault(c => c.Freshness != "Fresh") is { } stale
            ? $"{stale.CompanyName} data is {stale.AgeMinutes} minutes old (limit {MaxAgeMinutes})."
            : null;
}

/// <summary>Result of asking for a background load of an uncovered due-date range.</summary>
public sealed record ReceivablesRangeLoadDto(bool Accepted, bool AlreadyCovered, bool AlreadyRunning, string Message);

/// <summary>Where the time of one request went (milliseconds): the local SQL read, mapping in the application, and the total in the service.</summary>
public sealed record ServerTimingsDto(double SourceMs, double MapMs, double TotalMs);

using TigerCS.Application.Modules.Collections.Dto;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>One row of <c>CollectionsReceivableCompanyState</c> as returned by <c>usp_Collections_GetReceivables</c>.</summary>
public sealed record SnapshotCompanyRaw(
    int CompanyId, bool HasSnapshot, DateTime? LastSuccessUtc, DateTime? LastAttemptUtc, string? LastAttemptStatus,
    int? LastErrorNumber, int ConsecutiveFailures, int RowCount, DateOnly? CoverageFrom, DateOnly? CoverageThrough,
    int ExcludedInvalidUnitRows, decimal ExcludedInvalidUnitAmount, int ExcludedInvalidIdentityRows,
    decimal ExcludedInvalidIdentityAmount, int ContradictoryStatusRows, int UnknownStatusRows, bool RefreshInProgress = false, DateTime? RefreshStartedUtc = null, string? RunCompanyStatus = null,
    bool PaidRetained = false, bool BreakdownAvailable = false, int UnclassifiedRows = 0,
    DateTime? LastFinishedUtc = null, string? LastError = null, int? LastFetchMs = null, int? LastRawRows = null);

/// <summary>Freshness rules shared by the Receivables page, Campaigns and the export gate (pure, unit-tested).</summary>
public static class ReceivablesSnapshotStatusBuilder
{
    public const string Fresh = "Fresh", Stale = "Stale", Missing = "Missing";

    public static string CompanyName(int companyId) => companyId == 4 ? "Tiger Group Dubai" : "Tiger Group Sharjah";

    /// <summary>maxRunMinutes: the longest a refresh may legitimately execute (its SQL command timeout plus slack). A refresh still "Running" after this is reported as Stuck, not Loading; 0 = no limit.</summary>
    public static SnapshotCompanyStatusDto Company(SnapshotCompanyRaw raw, DateTime nowUtc, int maxAgeMinutes, int maxRunMinutes = 0)
    {
        var overdue = raw.RefreshInProgress && maxRunMinutes > 0 && raw.RefreshStartedUtc is { } started && (nowUtc - started).TotalMinutes > maxRunMinutes;
        var limit = Math.Max(1, maxAgeMinutes);
        int? age = raw.LastSuccessUtc is { } ok ? (int)Math.Max(0, Math.Floor((nowUtc - ok).TotalMinutes)) : null;
        // A success timestamp in the future (clock skew) is not trusted as fresh.
        var future = raw.LastSuccessUtc is { } t && t > nowUtc.AddMinutes(1);
        var freshness = !raw.HasSnapshot || raw.LastSuccessUtc is null ? Missing
            : future || age > limit ? Stale : Fresh;
        return new SnapshotCompanyStatusDto(raw.CompanyId, CompanyName(raw.CompanyId), raw.HasSnapshot,
            raw.LastSuccessUtc, raw.LastAttemptUtc, string.IsNullOrWhiteSpace(raw.LastAttemptStatus) ? "Never" : raw.LastAttemptStatus!,
            raw.LastErrorNumber, raw.ConsecutiveFailures, raw.RowCount, raw.CoverageFrom, raw.CoverageThrough,
            raw.ExcludedInvalidUnitRows, raw.ExcludedInvalidUnitAmount, raw.ExcludedInvalidIdentityRows, raw.ExcludedInvalidIdentityAmount,
            raw.ContradictoryStatusRows, raw.UnknownStatusRows, freshness, age, raw.RefreshInProgress, raw.RefreshStartedUtc, raw.RunCompanyStatus, raw.PaidRetained, raw.BreakdownAvailable, raw.UnclassifiedRows,
            raw.LastFinishedUtc, SnapshotFailureText.Sanitize(raw.LastErrorNumber, raw.LastError), raw.LastFetchMs, raw.LastRawRows, overdue);
    }

    public static SnapshotStatusDto Build(IEnumerable<SnapshotCompanyRaw> companies, IEnumerable<UnmatchedTowerDto> unmatched,
        DateTime nowUtc, int maxAgeMinutes, int maxRunMinutes = 0) =>
        new(companies.OrderBy(c => c.CompanyId).Select(c => Company(c, nowUtc, maxAgeMinutes, maxRunMinutes)).ToList(), unmatched.ToList(), Math.Max(1, maxAgeMinutes));
}

using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>
/// Turns what the local read procedure returned into a <see cref="PactReceivablesSnapshot"/>, or refuses. Pure and unit-tested:
/// the rule "missing data is an error, never an empty list" lives here.
/// </summary>
public static class ReceivablesSnapshotComposer
{
    /// <summary>Status of the snapshot for the requested range. Never throws: a company that was never loaded is a whole-range gap, not an empty result.</summary>
    public static SnapshotStatusDto BuildStatus(
        DateOnly from, DateOnly through, IReadOnlyList<SnapshotCompanyRaw> companies, IReadOnlyList<UnmatchedTowerDto> unmatched,
        DateTime nowUtc, int maxAgeMinutes)
    {
        var status = ReceivablesSnapshotStatusBuilder.Build(companies, unmatched, nowUtc, maxAgeMinutes);
        // Coverage gaps: the part of the requested range outside a company's coverage (or all of it when the company was never loaded).
        // It is REPORTED, never truncated silently and never presented as zero receivables.
        var gaps = new List<CoverageGapDto>();
        foreach (var c in status.Companies)
        {
            if (!c.HasSnapshot || c.CoverageFrom is not { } cf || c.CoverageThrough is not { } ct)
            { gaps.Add(new CoverageGapDto(c.CompanyId, c.CompanyName, from, through)); continue; }
            if (cf > from) gaps.Add(new CoverageGapDto(c.CompanyId, c.CompanyName, from, cf.AddDays(-1) < through ? cf.AddDays(-1) : through));
            if (ct < through) gaps.Add(new CoverageGapDto(c.CompanyId, c.CompanyName, ct.AddDays(1) > from ? ct.AddDays(1) : from, through));
        }
        return status with { RequestedFrom = from, RequestedThrough = through, CoverageGaps = gaps };
    }

    /// <summary>Oldest successful refresh among the loaded companies; the epoch when nothing was ever loaded (callers show "never").</summary>
    public static DateTime ReadAt(SnapshotStatusDto status) =>
        status.Companies.Where(c => c.HasSnapshot && c.LastSuccessUtc is not null).Select(c => c.LastSuccessUtc!.Value).DefaultIfEmpty(DateTime.UnixEpoch).Min();

    public static PactReceivablesSnapshot Compose(
        DateOnly from, DateOnly through, IReadOnlyList<SnapshotCompanyRaw> companies, IReadOnlyList<UnmatchedTowerDto> unmatched,
        IReadOnlyList<PactReceivableInstalment> rows, DateTime nowUtc, int maxAgeMinutes)
    {
        var status = BuildStatus(from, through, companies, unmatched, nowUtc, maxAgeMinutes);
        return new PactReceivablesSnapshot(rows, ReadAt(status), false, status);
    }
}

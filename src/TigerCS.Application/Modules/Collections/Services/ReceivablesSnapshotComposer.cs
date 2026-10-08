using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>
/// Turns what the local read procedure returned into a <see cref="PactReceivablesSnapshot"/>, or refuses. Pure and unit-tested:
/// the rule "missing data is an error, never an empty list" lives here.
/// </summary>
public static class ReceivablesSnapshotComposer
{
    public static PactReceivablesSnapshot Compose(
        DateOnly from, DateOnly through, IReadOnlyList<SnapshotCompanyRaw> companies, IReadOnlyList<UnmatchedTowerDto> unmatched,
        IReadOnlyList<PactReceivableInstalment> rows, DateTime nowUtc, int maxAgeMinutes)
    {
        var status = ReceivablesSnapshotStatusBuilder.Build(companies, unmatched, nowUtc, maxAgeMinutes);
        var loaded = status.Companies.Where(c => c.HasSnapshot).ToList();
        if (loaded.Count == 0)
            throw new PactReceivablesSourceException(
                "The receivables snapshot has not been loaded yet. The refresh has not completed successfully; this is not an empty result.");

        // Coverage gaps: the part of the requested range outside a loaded company's coverage. It is REPORTED (never truncated
        // silently and never presented as zero receivables); the rows that exist are still returned, flagged incomplete.
        var gaps = new List<CoverageGapDto>();
        foreach (var c in loaded)
        {
            if (c.CoverageFrom is not { } cf || c.CoverageThrough is not { } ct)
            { gaps.Add(new CoverageGapDto(c.CompanyId, c.CompanyName, from, through)); continue; }
            if (cf > from) gaps.Add(new CoverageGapDto(c.CompanyId, c.CompanyName, from, cf.AddDays(-1) < through ? cf.AddDays(-1) : through));
            if (ct < through) gaps.Add(new CoverageGapDto(c.CompanyId, c.CompanyName, ct.AddDays(1) > from ? ct.AddDays(1) : from, through));
        }
        status = status with { RequestedFrom = from, RequestedThrough = through, CoverageGaps = gaps };

        // A company that was never loaded stays visible in Snapshot (Freshness = Missing) and makes IsFresh false, so the
        // page warns and exports are refused; the loaded company's rows are still returned. ReadAtUtc is the OLDEST success.
        var readAt = loaded.Min(c => c.LastSuccessUtc!.Value);
        return new PactReceivablesSnapshot(rows, readAt, false, status);
    }
}

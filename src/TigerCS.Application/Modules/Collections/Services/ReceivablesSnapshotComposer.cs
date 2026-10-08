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

        // The requested window must be answerable from what was loaded; never silently truncate it.
        var uncovered = loaded.FirstOrDefault(c => (c.CoverageFrom is { } f && f > from) || (c.CoverageThrough is { } t && t < through));
        if (uncovered is not null)
            throw new PactReceivablesSourceException(
                $"The requested dates are outside the loaded snapshot coverage ({uncovered.CoverageFrom:yyyy-MM-dd} to {uncovered.CoverageThrough:yyyy-MM-dd}).");

        // A company that was never loaded stays visible in Snapshot (Freshness = Missing) and makes IsFresh false, so the
        // page warns and exports are refused; the loaded company's rows are still returned. ReadAtUtc is the OLDEST success.
        var readAt = loaded.Min(c => c.LastSuccessUtc!.Value);
        return new PactReceivablesSnapshot(rows, readAt, false, status);
    }
}

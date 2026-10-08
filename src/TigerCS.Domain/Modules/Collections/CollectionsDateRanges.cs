namespace TigerCS.Domain.Modules.Collections;

public static class CollectionsDateRanges
{
    /// <summary>
    /// "Last 6 months": six calendar months before the preview date through the preview date, both included. Crosses a year
    /// boundary naturally (2026-03-15 -> 2025-09-15..2026-03-15); a shorter target month clamps (2026-08-31 -> 2026-02-28).
    /// </summary>
    public static (DateOnly From, DateOnly To) LastSixMonths(DateOnly previewDate) => (previewDate.AddMonths(-6), previewDate);

    /// <summary>Sanity bounds of the date inputs. 1 January is only the DEFAULT From, never a minimum.</summary>
    public static bool IsSupported(DateOnly from, DateOnly to) => from <= to && from.Year >= 2000 && to.Year <= 2100;
}

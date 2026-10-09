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

    /// <summary>First and last day of a calendar month (handles leap February and year changes).</summary>
    public static (DateOnly From, DateOnly To) Month(int year, int month)
    {
        var first = new DateOnly(year, month, 1);
        return (first, first.AddMonths(1).AddDays(-1));
    }

    /// <summary>True when the range is exactly one whole calendar month (the Month / Year selectors then show it).</summary>
    public static bool TryAsCalendarMonth(DateOnly from, DateOnly to, out int year, out int month)
    {
        year = from.Year; month = from.Month;
        return from.Day == 1 && Month(from.Year, from.Month).To == to;
    }
}

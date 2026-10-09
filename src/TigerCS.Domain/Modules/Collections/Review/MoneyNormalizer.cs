using System.Globalization;

namespace TigerCS.Domain.Modules.Collections.Review;

public enum AmountPrecision
{
    /// <summary>Exactly representable in fils (two decimals).</summary>
    Exact = 0,

    /// <summary>A floating-point source value within tolerance of a fils amount; normalized to that amount.</summary>
    FloatingPointNoise,

    /// <summary>More than two decimals that cannot be explained as floating-point noise: the source must be corrected.</summary>
    Unresolved
}

/// <param name="Value">The amount in fils precision, or null when the precision is Unresolved.</param>
/// <param name="Raw">The value exactly as read from the source.</param>
/// <param name="Precision">How the value relates to a two-decimal amount.</param>
public sealed record MoneyResult(decimal? Value, decimal Raw, AmountPrecision Precision)
{
    public bool IsResolved => Precision != AmountPrecision.Unresolved;
}

/// <summary>
/// Monetary values are decimal throughout. Two-decimal rounding is applied only where it provably loses nothing:
/// a decimal source with more than two places is <b>unresolved</b> (never rounded to look clean), and a
/// floating-point source is snapped to fils only when it lies within the float tolerance of one — which is
/// representation noise, not money. A remainder that snaps to zero is a settled instalment.
/// </summary>
public static class MoneyNormalizer
{
    public const decimal DefaultFloatTolerance = 0.0001m;

    public static MoneyResult Normalize(decimal raw, bool sourceIsFloatingPoint, decimal floatTolerance = DefaultFloatTolerance)
    {
        var rounded = decimal.Round(raw, 2, MidpointRounding.AwayFromZero);
        if (raw == rounded) return new(rounded, raw, AmountPrecision.Exact);
        if (sourceIsFloatingPoint && Math.Abs(raw - rounded) <= floatTolerance)
            return new(rounded, raw, AmountPrecision.FloatingPointNoise);
        return new(null, raw, AmountPrecision.Unresolved);
    }

    /// <summary>Invariant, culture-independent, always two decimals, no thousands separator (the Genesys AmountDue format).</summary>
    public static string Format(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}

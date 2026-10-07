using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;

namespace TigerCS.Integrations.Modules.PactIntegration;

/// <summary>
/// Tolerant reader for PACT's contract dates. PACT does not guarantee one
/// date format: the same field has been observed as ISO-8601
/// (<c>"2026-01-01T00:00:00"</c>, with or without an offset), SQL-style
/// (<c>"2026-01-01 00:00:00"</c>), the legacy ASP.NET epoch wrapper
/// (<c>"/Date(1767225600000)/"</c>), a bare epoch number, and the
/// <c>dd/MM/yyyy</c> display form. Binding such a field as a bare
/// <c>DateTime?</c> once made an entire 200 response unparseable and
/// turned a found customer into "PACT unavailable" — so this parser
/// <b>never throws</b>: anything it cannot read becomes <c>null</c>, and
/// the rest of the row (and the lookup) carries on untouched.
///
/// <para>
/// The result is a calendar date, read exactly as PACT wrote it: a textual
/// date keeps its own day with no time-zone shift (PACT's dates are UAE
/// business dates already). Only an epoch value, which is a UTC instant
/// rather than a date, is converted to the Dubai calendar date it falls on.
/// </para>
/// </summary>
public static partial class PactContractDateParser
{
    private static readonly string[] TextualFormats =
    [
        // ISO-8601 date and date-time, with and without fractional seconds
        // (the "s" and "o" standard formats are strict; the explicit
        // patterns let a trailing "Z"/offset and fractions bind too).
        "yyyy-MM-dd",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd'T'HH:mm",
        // SQL-style, space separator.
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm",
        // Display forms seen in PACT exports. Day-first: PACT is a UAE
        // system and "01/02/2026" is 1 February, never 2 January.
        "dd/MM/yyyy",
        "d/M/yyyy",
        "dd/MM/yyyy HH:mm:ss",
        "d/M/yyyy HH:mm:ss",
        "dd-MM-yyyy",
        "d-M-yyyy",
        "dd-MM-yyyy HH:mm:ss",
        "yyyyMMdd"
    ];

    private static readonly Lazy<TimeZoneInfo> DubaiZone =
        new(() => TimeZoneInfo.FindSystemTimeZoneById(PactContractActivity.DubaiTimeZoneId));

    [GeneratedRegex(@"^/Date\((-?\d+)(?:[+-]\d{4})?\)/$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyEpochWrapper();

    /// <summary>Reads a textual PACT date; null for a blank or unreadable value, never an exception.</summary>
    public static DateOnly? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        var epochMatch = LegacyEpochWrapper().Match(text);
        if (epochMatch.Success)
        {
            return long.TryParse(epochMatch.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var epochMilliseconds)
                ? FromEpochMilliseconds(epochMilliseconds)
                : null;
        }

        // Textual forms, with or without a time or an explicit offset: the
        // written calendar day is kept as-is (an offset belongs to PACT's
        // own clock, not to a conversion the caller asked for).
        if (DateTimeOffset.TryParseExact(
                text, TextualFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var withOffset))
        {
            return DateOnly.FromDateTime(withOffset.DateTime);
        }

        // A bare epoch (milliseconds, or seconds for a small value) sent as text.
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var bareEpoch))
        {
            return FromEpoch(bareEpoch);
        }

        return null;
    }

    /// <summary>Reads a PACT date sent as a JSON number — an epoch in milliseconds (or seconds, for a small value).</summary>
    public static DateOnly? TryParse(long epoch) => FromEpoch(epoch);

    private static DateOnly? FromEpoch(long epoch) =>
        // Anything below 10^11 cannot be a plausible millisecond epoch
        // (that is March 1973); PACT's dates are decades later, so a
        // small value is an epoch in seconds.
        epoch is > -100_000_000_000L and < 100_000_000_000L ? FromEpochSeconds(epoch) : FromEpochMilliseconds(epoch);

    private static DateOnly? FromEpochMilliseconds(long milliseconds)
    {
        try
        {
            return ToDubaiDate(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateOnly? FromEpochSeconds(long seconds)
    {
        try
        {
            return ToDubaiDate(DateTimeOffset.FromUnixTimeSeconds(seconds));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateOnly ToDubaiDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, DubaiZone.Value).DateTime);
}

/// <summary>
/// <see cref="JsonConverter{T}"/> over <see cref="PactContractDateParser"/>
/// for the PACT wire DTOs: a string, a number, <c>null</c>, or any other
/// token (an object, an array, a boolean) binds to a <see cref="DateOnly"/>
/// or <c>null</c> — never a <see cref="JsonException"/>, so one odd date
/// cannot take the whole contracts response down with it.
/// </summary>
public sealed class PactContractDateJsonConverter : JsonConverter<DateOnly?>
{
    public override bool HandleNull => true;

    public override DateOnly? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return PactContractDateParser.TryParse(reader.GetString());
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var epoch) ? PactContractDateParser.TryParse(epoch) : null;
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                // Consume the whole nested value so the reader is left
                // positioned correctly for the next property.
                reader.Skip();
                return null;
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, DateOnly? value, JsonSerializerOptions options)
    {
        if (value is { } date)
        {
            writer.WriteStringValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

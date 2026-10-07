using System.Text.Json;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Integrations.Modules.PactIntegration;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.CustomerVerification.Services;

/// <summary>
/// <see cref="PactContractDateParser"/> reads every date format PACT has
/// been seen to emit and never throws for anything else, and
/// <see cref="PactContractActivity"/> applies the one expiry rule on the
/// Dubai calendar date. Both are exercised directly here; their use in the
/// gateway and the lookup layer is covered by
/// <see cref="PactCustomerHttpGatewayTests"/> and
/// <c>CustomerLookupAppServiceTests</c>.
/// </summary>
public class PactContractDateParserTests
{
    [Theory]
    [InlineData("2026-01-01T00:00:00", 2026, 1, 1)]
    [InlineData("2026-01-01T00:00:00.1234567", 2026, 1, 1)]
    [InlineData("2026-03-15T23:59:59Z", 2026, 3, 15)] // the written day is kept — no shift to Dubai's 16th
    [InlineData("2026-03-15T00:30:00+04:00", 2026, 3, 15)]
    [InlineData("2026-03-15T22:30:00-05:00", 2026, 3, 15)]
    [InlineData("2026-01-01", 2026, 1, 1)]
    [InlineData("2026-01-01 00:00:00", 2026, 1, 1)]
    [InlineData("2026-01-01 00:00:00.000", 2026, 1, 1)]
    [InlineData("2026-01-01 13:45", 2026, 1, 1)]
    [InlineData("  2026-01-01T00:00:00  ", 2026, 1, 1)]
    [InlineData("31/12/2026", 2026, 12, 31)]
    [InlineData("01/02/2026", 2026, 2, 1)] // day first: 1 February
    [InlineData("1/2/2026", 2026, 2, 1)]
    [InlineData("31/12/2026 23:59:59", 2026, 12, 31)]
    [InlineData("31-12-2026", 2026, 12, 31)]
    [InlineData("20261231", 2026, 12, 31)]
    [InlineData("/Date(1767225600000)/", 2026, 1, 1)] // 2026-01-01T00:00:00Z → 04:00 in Dubai, same day
    [InlineData("/Date(1767211200000)/", 2026, 1, 1)] // 2025-12-31T20:00:00Z → already 1 January in Dubai
    [InlineData("/Date(1767225600000+0400)/", 2026, 1, 1)]
    [InlineData("1767225600000", 2026, 1, 1)] // bare epoch milliseconds as text
    [InlineData("1767225600", 2026, 1, 1)] // bare epoch seconds as text
    public void TryParse_KnownPactFormats_YieldTheCalendarDate(string value, int year, int month, int day)
    {
        Assert.Equal(new DateOnly(year, month, day), PactContractDateParser.TryParse(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    [InlineData("2026-13-45")]
    [InlineData("2026-02-30")]
    [InlineData("32/01/2026")]
    [InlineData("/Date(abc)/")]
    [InlineData("/Date()/")]
    [InlineData("Date(1767225600000)")]
    [InlineData("1767225600000x")]
    [InlineData("-9223372036854775808")]
    [InlineData("99999999999999999999")] // overflows long
    [InlineData("/Date(999999999999999999)/")] // out of DateTimeOffset's range
    public void TryParse_UnreadableValues_YieldNullNeverThrow(string? value)
    {
        Assert.Null(PactContractDateParser.TryParse(value));
    }

    [Theory]
    [InlineData(1767225600000L, 2026, 1, 1)] // milliseconds
    [InlineData(1767225600L, 2026, 1, 1)] // seconds
    public void TryParse_EpochNumber_YieldsTheDubaiDate(long epoch, int year, int month, int day)
    {
        Assert.Equal(new DateOnly(year, month, day), PactContractDateParser.TryParse(epoch));
    }

    private sealed record Row(
        [property: System.Text.Json.Serialization.JsonConverter(typeof(PactContractDateJsonConverter))] DateOnly? ContractEndDate,
        string? After);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    [Theory]
    [InlineData("\"2026-01-01T00:00:00\"", "2026-01-01")]
    [InlineData("1767225600000", "2026-01-01")]
    [InlineData("null", null)]
    [InlineData("-9223372036854775808", null)]
    [InlineData("\"garbage\"", null)]
    [InlineData("true", null)]
    [InlineData("12.5", null)]
    [InlineData("{ \"nested\": { \"deeper\": [1, 2] } }", null)]
    [InlineData("[\"2026-01-01\"]", null)]
    public void JsonConverter_AnyToken_BindsWithoutThrowingAndLeavesTheReaderPositioned(string jsonValue, string? expected)
    {
        var row = JsonSerializer.Deserialize<Row>($$"""{ "contractEndDate": {{jsonValue}}, "after": "still read" }""", JsonOptions);

        Assert.NotNull(row);
        Assert.Equal(expected is null ? null : DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), row.ContractEndDate);
        // The property AFTER the odd value still binds — the converter
        // consumed exactly its own token(s), nothing more or less.
        Assert.Equal("still read", row.After);
    }

    [Fact]
    public void JsonConverter_MissingProperty_IsNull()
    {
        var row = JsonSerializer.Deserialize<Row>("""{ "after": "x" }""", JsonOptions);

        Assert.Null(row!.ContractEndDate);
    }

    // ---------------------------------------------------------------
    // PactContractActivity: the expiry rule on the Dubai calendar date.
    // ---------------------------------------------------------------

    private static PactContractDto ContractEnding(DateOnly? endDate) =>
        new("U-1", "C-1", "0101", "Project", "Residential", ContractEndDate: endDate);

    [Fact]
    public void TodayInDubai_ConvertsTheUtcClockToDubaiCalendarDate()
    {
        // 21:30 UTC on 5 October is 01:30 on 6 October in Dubai (UTC+4).
        Assert.Equal(new DateOnly(2026, 10, 6), PactContractActivity.TodayInDubai(new FakeTimeProvider(new DateTime(2026, 10, 5, 21, 30, 0, DateTimeKind.Utc))));
        // 19:30 UTC on 5 October is still 23:30 on 5 October in Dubai.
        Assert.Equal(new DateOnly(2026, 10, 5), PactContractActivity.TodayInDubai(new FakeTimeProvider(new DateTime(2026, 10, 5, 19, 30, 0, DateTimeKind.Utc))));
    }

    [Fact]
    public void IsActiveOn_EndedYesterday_IsExpired()
    {
        var today = new DateOnly(2026, 10, 6);
        Assert.False(PactContractActivity.IsActiveOn(ContractEnding(today.AddDays(-1)), today));
    }

    [Fact]
    public void IsActiveOn_EndsToday_IsActive()
    {
        var today = new DateOnly(2026, 10, 6);
        Assert.True(PactContractActivity.IsActiveOn(ContractEnding(today), today));
    }

    [Fact]
    public void IsActiveOn_EndsInFuture_IsActive()
    {
        var today = new DateOnly(2026, 10, 6);
        Assert.True(PactContractActivity.IsActiveOn(ContractEnding(today.AddDays(1)), today));
    }

    [Fact]
    public void IsActiveOn_NoReadableEndDate_IsActive()
    {
        Assert.True(PactContractActivity.IsActiveOn(ContractEnding(null), new DateOnly(2026, 10, 6)));
    }
}

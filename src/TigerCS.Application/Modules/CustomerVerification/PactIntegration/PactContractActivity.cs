namespace TigerCS.Application.Modules.CustomerVerification.PactIntegration;

/// <summary>
/// The one rule for whether a PACT contract is still current: a contract
/// whose <see cref="PactContractDto.ContractEndDate"/> falls before today's
/// Dubai calendar date has expired; one ending today or later is active.
///
/// <para>
/// <b>"Today" is the Dubai calendar date, never the server's.</b> PACT's
/// dates are UAE business dates, and the API may run on a UTC host where,
/// between 20:00 and midnight UTC, the server date is still "yesterday" in
/// Dubai — a contract ending on that Dubai date must already count as
/// active-today, not as tomorrow's. <see cref="TodayInDubai"/> is the
/// single place that conversion happens for PACT contract filtering.
/// </para>
///
/// <para>
/// <b>A missing or unreadable date never hides a contract.</b> The gateway
/// leaves <c>ContractEndDate</c> null for an absent or malformed PACT value
/// (see <c>PactContractDateParser</c>); treating that as "expired" would
/// hide real, current contracts over a formatting quirk, so a null is
/// always active — the agent sees it exactly as before this rule existed.
/// </para>
///
/// <para>
/// Applied only by the customer lookup/list layer
/// (<c>CustomerLookupAppService</c>'s PACT leg), so the New Ticket wizard
/// and the Customer Workspace stop offering expired units — while the
/// Collections account mapping (which needs every contract, expired or not,
/// to resolve a tenant's EDSM accounts) and persisted ticket history keep
/// reading the gateway unfiltered.
/// </para>
/// </summary>
public static class PactContractActivity
{
    /// <summary>IANA id of the UAE's time zone — the same id <c>CollectionsOptions.TimeZoneId</c> defaults to.</summary>
    public const string DubaiTimeZoneId = "Asia/Dubai";

    private static readonly Lazy<TimeZoneInfo> DubaiZone = new(() => TimeZoneInfo.FindSystemTimeZoneById(DubaiTimeZoneId));

    /// <summary>Today's calendar date in Dubai, taken from <paramref name="timeProvider"/>'s UTC clock.</summary>
    public static DateOnly TodayInDubai(TimeProvider timeProvider) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(timeProvider.GetUtcNow().UtcDateTime, DubaiZone.Value));

    /// <summary>
    /// True when the contract is still current on <paramref name="today"/>:
    /// it ends today or later, or carries no readable end date at all.
    /// Only an end date strictly before <paramref name="today"/> expires it.
    /// </summary>
    public static bool IsActiveOn(PactContractDto contract, DateOnly today) =>
        contract.ContractEndDate is not { } endDate || endDate >= today;
}

namespace TigerCS.Application.Modules.Collections;

/// <summary>
/// The EDSM part of the <c>CollectionsSource</c> section. Every default is
/// the safe one: no provider, no number culture (so no formatted amount is
/// read), and due-installments off (its response is company-wide and unpaged).
/// </summary>
public sealed class CollectionsEdsmOptions
{
    public const string SectionName = "CollectionsSource";

    /// <summary>"Unavailable" (default), "Pact" (the real EDSM routes on PactApi) or "Fixture" (Development/Testing only).</summary>
    public string EdsmProvider { get; set; } = "Unavailable";

    /// <summary>
    /// The culture of the EDSM host process, which formats every amount as <c>#,##0.00</c>
    /// and every transaction date as <c>dd-MMM-yyyy</c> (contract §7). UNVERIFIED for
    /// production: set it (e.g. "en-US") only once the IIS host's culture is confirmed.
    /// Blank = no formatted string is read.
    /// </summary>
    public string? EdsmNumberCulture { get; set; }

    /// <summary>EDSM returns no currency; amounts are implicitly AED (contract §7). Shown as configured by TigerCS, not as returned.</summary>
    public string Currency { get; set; } = "AED";

    /// <summary>Read-only payment-transactions (types 1-3) on the Payment tab.</summary>
    public bool TransactionsEnabled { get; set; } = true;

    /// <summary>Due-installments returns every tenant of the company for the range, unpaged. Off by default.</summary>
    public bool DueInstallmentsEnabled { get; set; }

    public int DueInstallmentsLookbackDays { get; set; } = 31;

    public int DueInstallmentsLookaheadDays { get; set; } = 31;

    /// <summary>
    /// The longest a posted payment can take to appear (contract §8.3: 10-minute ledger
    /// cache + 10-minute summary cache in the source config; deployed values UNVERIFIED).
    /// </summary>
    public int MaxSourceDelayMinutes { get; set; } = 20;

    /// <summary>
    /// How long a verified PACT account mapping (company/tenant pairs) is reused before
    /// <c>v1/contracts/{mobile}</c>, which writes inside EDSM, is called again. Clamped to 1–1440.
    /// </summary>
    public int PactMappingTtlMinutes { get; set; } = 30;

    /// <summary>How long "PACT returned no contracts for this tenant" is reused. Clamped to 1–60.</summary>
    public int PactMappingNegativeTtlMinutes { get; set; } = 5;

    /// <summary>The cache, as EDSM's own config sets it per entry (contract §8.1). Shown, not enforced.</summary>
    public int SourceCacheMinutes { get; set; } = 10;

    /// <summary>
    /// Overall budget, in seconds, for one Genesys payment read (<c>api/genesys/collections</c>):
    /// PACT discovery and every EDSM call together. It must end before TigerGroupWeb's own
    /// timeout, which must end before the Genesys flow's (docs/Collections/Genesys-Collections-API.md §1.2).
    /// Clamped to 1–<see cref="MaxReadDeadlineSeconds"/>.
    /// </summary>
    public int GenesysReadDeadlineSeconds { get; set; } = 22;

    /// <summary>The same budget for the Payment tab (<c>api/collections</c>), which also loads transactions by default.</summary>
    public int WebReadDeadlineSeconds { get; set; } = 60;

    public const int MaxReadDeadlineSeconds = 300;

    /// <summary>A caller may ask for a shorter budget with this header (whole seconds); never a longer one.</summary>
    public const string DeadlineHeader = "X-Collections-Deadline-Seconds";

    /// <summary>
    /// The budget for one read: the surface's configured value, shortened (never lengthened)
    /// by a positive <paramref name="requestedSeconds"/>.
    /// </summary>
    public TimeSpan ReadDeadline(CollectionsReadSurface surface, int? requestedSeconds = null)
    {
        var configured = Math.Clamp(
            surface == CollectionsReadSurface.Genesys ? GenesysReadDeadlineSeconds : WebReadDeadlineSeconds,
            1, MaxReadDeadlineSeconds);
        var seconds = requestedSeconds is > 0 ? Math.Min(configured, requestedSeconds.Value) : configured;
        return TimeSpan.FromSeconds(seconds);
    }
}

/// <summary>Which route prefix a Collections read came through; selects its default deadline.</summary>
public enum CollectionsReadSurface
{
    Web,
    Genesys
}

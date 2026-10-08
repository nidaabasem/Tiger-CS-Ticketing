namespace TigerCS.Application.Modules.SlaAndEscalation.Services;

/// <summary>
/// Configuration of the priority-downgrade approval workflow. Bound from
/// <see cref="SectionName"/>. The contract's 24-hour expiry is an
/// <c>[ASSUMPTION]</c> (MVP-ERD.md section 2.27); the default here is 7 days
/// so a request is not lost over a weekend, and it is configurable rather
/// than hard-coded.
/// </summary>
public sealed class PriorityDowngradeOptions
{
    public const string SectionName = "SlaAndEscalation:PriorityDowngrade";

    public const int DefaultExpiryHours = 7 * 24;

    /// <summary>How long a Pending request stays decidable. Non-positive values fall back to the default.</summary>
    public int ExpiryHours { get; set; } = DefaultExpiryHours;

    public TimeSpan Lifetime => TimeSpan.FromHours(ExpiryHours > 0 ? ExpiryHours : DefaultExpiryHours);
}

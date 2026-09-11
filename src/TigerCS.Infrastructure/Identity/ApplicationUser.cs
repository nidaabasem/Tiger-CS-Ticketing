using Microsoft.AspNetCore.Identity;

namespace TigerCS.Infrastructure.Identity;

/// <summary>
/// ASP.NET Core Identity's TUser (ADR-0004). Carries no domain fields — the
/// Employee entity extends AspNetUsers 1:1 for staff-specific attributes
/// (DisplayName, IsGeynessStaff, DeactivatedAtUtc), exactly as ADR-0004
/// specifies.
///
/// <para>
/// <b>The one exception is external identity mapping.</b> The Genesys agent
/// identity mapping lives here rather than on Employee because it is an
/// identity concern: which authenticated Ticketing account a Genesys agent
/// corresponds to. <see cref="GenesysUserId"/> is the canonical key — the
/// immutable Genesys User ID, never the agent's display name — and is unique
/// among non-null values (<c>UX_AspNetUsers_GenesysUserId</c>, filtered).
/// <see cref="GenesysEmail"/> is informational only and is never used to
/// resolve an agent. Nothing creates a user from a Genesys request: an
/// unmapped agent is a controlled failure, and mapping is an administrative
/// act (see <c>docs/architecture/Genesys-API-Contracts.md</c>).
/// </para>
/// </summary>
public class ApplicationUser : IdentityUser<Guid>
{
    public const int GenesysUserIdMaxLength = 64;
    public const int GenesysEmailMaxLength = 256;

    /// <summary>The immutable Genesys User ID this Ticketing user corresponds to, or null when the user is not a Genesys agent. The only key agent resolution uses.</summary>
    public string? GenesysUserId { get; set; }

    /// <summary>The agent's email as known to Genesys — informational / support-facing only. Never an identity key.</summary>
    public string? GenesysEmail { get; set; }
}

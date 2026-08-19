using Microsoft.AspNetCore.Identity;

namespace TigerCS.Infrastructure.Identity;

/// <summary>
/// ASP.NET Core Identity's TUser (ADR-0004). Deliberately carries no extra
/// domain fields — the Employee entity extends AspNetUsers 1:1 for
/// staff-specific attributes (DisplayName, IsGeynessStaff, DeactivatedAtUtc),
/// exactly as ADR-0004 specifies.
/// </summary>
public class ApplicationUser : IdentityUser<Guid>;

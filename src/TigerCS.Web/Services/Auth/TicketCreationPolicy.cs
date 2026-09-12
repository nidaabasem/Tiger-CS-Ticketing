using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Web.Services.Auth;

/// <summary>
/// Whether the "+ New Ticket" action is shown — a mirror of the roles the
/// Api's <c>CustomerVerification</c> policy (which gates <c>POST api/tickets</c>)
/// accepts: CS Agent and CS Supervisor, plus the System Administrator override
/// that satisfies every Api policy. Display-only; the Api is the enforcement
/// point, and the New Ticket page itself is unchanged.
/// </summary>
public static class TicketCreationPolicy
{
    public static readonly IReadOnlyList<string> AllowedRoles =
    [
        Roles.CsAgent,
        Roles.CsSupervisor,
        Roles.SystemAdministrator,
    ];

    public static bool AppliesTo(CurrentUser? user) =>
        user is not null && user.Roles.Any(role => AllowedRoles.Contains(role, StringComparer.Ordinal));
}

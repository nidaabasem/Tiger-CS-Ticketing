using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Web.Models;

/// <summary>
/// Display-side action affordance checks. These decide only whether a
/// control is worth rendering — the Api's own authorization
/// (TicketRoleSets + the System Administrator override) remains the
/// enforcement point, and a control shown here can still come back 403.
/// </summary>
public static class TicketActions
{
    /// <summary>
    /// Mirrors TicketRoleSets.Reopen (ISSUE-022: the CS layer reopens) plus
    /// the System Administrator override — the roles for which a Reopen
    /// control is worth showing at all. Lifecycle eligibility (Resolved/
    /// Closed, within the reopen window) is the server-computed
    /// IsReopenEligible flag, never re-derived here.
    /// </summary>
    private static readonly string[] ReopenRoles =
    [
        Roles.CsAgent, Roles.CsSupervisor, Roles.CsManager, Roles.SystemAdministrator
    ];

    public static bool CanReopen(IReadOnlyCollection<string>? viewerRoles) =>
        viewerRoles is not null && viewerRoles.Any(ReopenRoles.Contains);
}

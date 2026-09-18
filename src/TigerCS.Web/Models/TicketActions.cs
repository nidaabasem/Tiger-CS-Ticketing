using TigerCS.Application.Modules.Ticketing.Services;
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
    /// Reads the Api's own <see cref="TicketRoleSets.Reopen"/> — CS Agent
    /// under the approved rule — rather than a local copy, plus the System
    /// Administrator override (ADR-0024) the Api honors through its
    /// AuthorizationGate. Lifecycle eligibility (Closed, closed as Resolved,
    /// inside the reopen window) is the server-computed IsReopenEligible flag,
    /// never re-derived here.
    ///
    /// <para>
    /// Display only, and deliberately <i>narrower</i> than the server's rule:
    /// the endpoint additionally requires the caller to have access to the
    /// ticket, which this cannot see. A control shown here can still come back
    /// 403, which is the correct direction for a UI check to be wrong in.
    /// </para>
    /// </summary>
    public static bool CanReopen(IReadOnlyCollection<string>? viewerRoles) =>
        viewerRoles is not null
        && (viewerRoles.Any(TicketRoleSets.Reopen.Contains) || viewerRoles.Contains(Roles.SystemAdministrator));

    /// <summary>
    /// Whether a Transfer control — and the department picker inside it —
    /// is worth rendering. Reads the Api's own <see cref="TicketRoleSets.Transfer"/>
    /// (CS Manager only) rather than a local copy, so the UI can never offer
    /// transfer destinations to a role the Api would refuse; plus the
    /// System Administrator override (ADR-0024) the Api honors through its
    /// AuthorizationGate. Enforcement stays server-side.
    /// </summary>
    public static bool CanTransfer(IReadOnlyCollection<string>? viewerRoles) =>
        viewerRoles is not null
        && (viewerRoles.Any(TicketRoleSets.Transfer.Contains) || viewerRoles.Contains(Roles.SystemAdministrator));
}

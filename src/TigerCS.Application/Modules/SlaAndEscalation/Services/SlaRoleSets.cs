using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.SlaAndEscalation.Services;

/// <summary>
/// Per-action role sets for SLA and escalation authorization, mirroring
/// <c>TicketRoleSets</c>'s design exactly: each action gets its own set,
/// stating what the permission matrix grants, and every consumer routes its
/// decision through <c>AuthorizationGate</c>.
///
/// <para>
/// <b>System Administrator is deliberately absent from every set below, and
/// is authorized anyway.</b> ADR-0024's central override is applied by the
/// gate before any set here is consulted, and by
/// <c>SystemAdministratorOverrideHandler</c> for the policy layer. Naming
/// the role in these lists is exactly the per-call-site duplication that
/// correction's requirement 4 rules out — and it would erase the distinction
/// these sets exist to record: what the permission matrix grants, as against
/// what the override grants on top of it.
/// </para>
/// </summary>
public static class SlaRoleSets
{
    /// <summary>
    /// Record the First Human Response (MVP-API-Contracts.md §5.2, "Agent and
    /// above") — the ticket's current owner, or a supervisory role. Ownership
    /// is checked separately against the ticket itself, exactly as
    /// <c>TicketLifecycleAppService</c> does for a status change; this set is
    /// the cross-department authority that stands in for it.
    /// </summary>
    public static readonly IReadOnlyCollection<string> RecordFirstResponse =
    [
        Roles.CsSupervisor, Roles.CsManager, Roles.GeneralManager, Roles.ChairmanCeo
    ];

    /// <summary>
    /// Raise a manual escalation with <c>ManualFlag</c>
    /// (MVP-API-Contracts.md §5.7, "Agent and above"). Includes the
    /// department-side roles because FR-ESC-01's flag-for-escalation is the
    /// action a working agent takes when they cannot resolve a ticket
    /// themselves (Solution-Analysis.md §7.6, Level 1), and a Department
    /// Employee working the ticket is exactly that actor. Department-scoped
    /// callers are additionally checked against the ticket's own department.
    /// </summary>
    public static readonly IReadOnlyCollection<string> ManualEscalate =
    [
        Roles.CsAgent, Roles.CsSupervisor, Roles.CsManager,
        Roles.DepartmentEmployee, Roles.DepartmentHead,
        Roles.GeneralManager, Roles.ChairmanCeo
    ];

    /// <summary>
    /// Level 4 (Chairman/CEO) escalation: <b>CS Manager or GM only</b>
    /// (MVP-ERD.md §2.17 — "`TriggerType = ManualLevel4` rows may only be
    /// inserted by a CS Manager or GM actor"; MVP-API-Contracts.md §5.7's
    /// `403` validation rule). Deliberately narrower than
    /// <see cref="ManualEscalate"/>, and never widened by a level check
    /// alone — see <c>TicketEscalationAppService</c>.
    /// </summary>
    public static readonly IReadOnlyCollection<string> ManualLevel4Escalate = [Roles.CsManager, Roles.GeneralManager];
}

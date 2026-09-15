using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The one definition of "may this caller see this ticket at all" —
/// Security-Architecture.md §3's rule that access is "evaluated against the
/// ticket's current data, not cached or assumed from the user's session
/// alone": a CS-layer/executive role sees every department
/// (<see cref="TicketRoleSets.CrossDepartmentView"/>), everyone else sees only
/// departments they are actually assigned to.
///
/// <para>
/// Extracted from <c>TicketQueryAppService</c> (which still owns the read
/// paths and simply delegates here) when the approved Reopen rule added a
/// resource-level check to a mutation. Two call sites evaluating ticket
/// visibility is fine; two call sites each spelling the rule out is how they
/// drift apart, and a visibility rule that drifts is a disclosure bug.
/// </para>
///
/// <para>
/// Routed through <see cref="AuthorizationGate"/>, so the ADR-0024 System
/// Administrator override applies here exactly as it does everywhere else —
/// and, as the gate's async overload guarantees, without a
/// department-membership round trip for an overridden caller.
/// </para>
/// </summary>
public static class TicketVisibilityRule
{
    public static Task<bool> CanViewDepartmentAsync(
        IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        int departmentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userDepartmentAssignmentRepository);

        return AuthorizationGate.EvaluateAsync(callerRoles, async () =>
            callerRoles.Any(TicketRoleSets.CrossDepartmentView.Contains)
            || await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, departmentId, cancellationToken));
    }
}

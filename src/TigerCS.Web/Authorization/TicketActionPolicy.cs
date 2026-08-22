using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Web.Authorization;

/// <summary>
/// What the UI should offer a given user on a given ticket.
///
/// <para>
/// <b>This is presentation, not authorization.</b> TigerCS.Api decides every
/// one of these questions again, from the caller's own token and the ticket's
/// current data, and its answer is the only one that counts. A user who
/// reaches a hidden action by any means still gets 403 from the API, and the
/// pages here render that 403 rather than assuming it cannot happen. Nothing
/// in this file is consulted before showing a result the server already
/// returned.
/// </para>
///
/// <para>
/// <b>Why mirror the rules at all, then.</b> Because an action bar of twelve
/// buttons that mostly 403 is a worse tool than one showing the four this user
/// can actually use. The rules below are a deliberate, traced copy of
/// <c>TicketRoleSets</c> and the resource checks in
/// <c>TicketAssignmentAppService</c>/<c>TicketLifecycleAppService</c>, and the
/// duplication is confined to this one file so it can be checked against the
/// server in one place - <c>TicketActionPolicyMirrorsServerTests</c> does
/// exactly that, reading the server's own role-set constants rather than a
/// second copy of them.
/// </para>
///
/// <para>
/// System Administrator passes everything, matching ADR-0024's central
/// override.
/// </para>
/// </summary>
public static class TicketActionPolicy
{
    /// <summary>Whether the ticket is in its terminal, read-only state.</summary>
    public static bool IsClosed(TicketDetailDto ticket) =>
        string.Equals(ticket.TicketStatus, "Closed", StringComparison.Ordinal);

    /// <summary>Whether the ticket still awaits CRM reconciliation.</summary>
    public static bool IsPendingCrm(string verificationStatus) =>
        string.Equals(verificationStatus, "PendingCrmVerification", StringComparison.Ordinal);

    /// <summary>
    /// Assign/reassign. CS Supervisor and Department Head may act only within a
    /// department they belong to; CS Manager may act across departments. CS
    /// Agent and Department Employee hold no assignment capability at all,
    /// not even self-claim.
    /// </summary>
    public static bool CanAssign(UserContext user, TicketDetailDto ticket)
    {
        if (IsClosed(ticket))
        {
            return false;
        }

        return user.IsSystemAdministrator
            || user.HasRole(Roles.CsManager)
            || ((user.HasRole(Roles.CsSupervisor) || user.HasRole(Roles.DepartmentHead))
                && user.BelongsToDepartment(ticket.CurrentDepartmentId));
    }

    /// <summary>Transfer to another department. CS Manager only.</summary>
    public static bool CanTransfer(UserContext user, TicketDetailDto ticket) =>
        !IsClosed(ticket) && (user.IsSystemAdministrator || user.HasRole(Roles.CsManager));

    /// <summary>
    /// Move within the working sub-machine. The ticket's current owner, or a
    /// cross-department supervisory role, or a Department Head in the ticket's
    /// own department.
    /// </summary>
    public static bool CanChangeStatus(UserContext user, TicketDetailDto ticket)
    {
        if (IsClosed(ticket))
        {
            return false;
        }

        return user.IsSystemAdministrator
            || ticket.CurrentOwnerEmployeeId == user.EmployeeId
            || user.HasAnyRole(Roles.CsSupervisor, Roles.CsManager, Roles.GeneralManager, Roles.ChairmanCeo)
            || (user.HasRole(Roles.DepartmentHead) && user.BelongsToDepartment(ticket.CurrentDepartmentId));
    }

    /// <summary>
    /// Resolve. Department Employee (only their own ticket) or Department Head
    /// (any ticket in a department they belong to). Never a CS-layer role -
    /// ISSUE-022's approved Resolve/Close split.
    /// </summary>
    public static bool CanResolve(UserContext user, TicketDetailDto ticket)
    {
        if (IsClosed(ticket))
        {
            return false;
        }

        if (user.IsSystemAdministrator)
        {
            return true;
        }

        if (user.HasRole(Roles.DepartmentHead))
        {
            return user.BelongsToDepartment(ticket.CurrentDepartmentId);
        }

        return user.HasRole(Roles.DepartmentEmployee) && ticket.CurrentOwnerEmployeeId == user.EmployeeId;
    }

    /// <summary>
    /// Close. CS Agent, CS Supervisor or CS Manager - the other half of
    /// ISSUE-022's split. Cross-department, since CS is inherently so.
    /// </summary>
    public static bool CanClose(UserContext user, TicketDetailDto ticket) =>
        !IsClosed(ticket)
        && (user.IsSystemAdministrator || user.HasAnyRole(Roles.CsAgent, Roles.CsSupervisor, Roles.CsManager));

    /// <summary>
    /// CRM reconciliation of a provisional ticket. Gated by the API's
    /// <c>CustomerVerification</c> policy, and only meaningful while the ticket
    /// is still PendingCrmVerification.
    /// </summary>
    public static bool CanReconcile(UserContext user, TicketDetailDto ticket) =>
        !IsClosed(ticket)
        && IsPendingCrm(ticket.VerificationStatus)
        && CanCreateTickets(user);

    /// <summary>
    /// Whether the user may run the intake, CRM lookup, verification and ticket
    /// creation endpoints - the API's <c>CustomerVerification</c> policy, which
    /// is CS Agent and CS Supervisor only.
    /// </summary>
    public static bool CanCreateTickets(UserContext user) =>
        user.IsSystemAdministrator || user.HasAnyRole(Roles.CsAgent, Roles.CsSupervisor);

    /// <summary>
    /// Whether the user may add a note. Any authenticated staff member who can
    /// see the ticket may, and a closed ticket is read-only.
    /// </summary>
    public static bool CanAddNote(TicketDetailDto ticket) => !IsClosed(ticket);
}

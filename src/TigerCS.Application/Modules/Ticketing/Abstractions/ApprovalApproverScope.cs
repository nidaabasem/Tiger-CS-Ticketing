using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>
/// Everything a repository needs to decide, SQL-side, which Pending
/// approvals one caller is authorized to action — the set-based twin of
/// <c>TicketApprovalAppService.IsAuthorizedApproverAsync</c>'s per-record
/// rule, resolved once per request from the caller's own roles and
/// department memberships (never client-supplied):
/// <list type="bullet">
/// <item>Employee target: the target employee is the caller.</item>
/// <item>Role target: the caller holds the target role.</item>
/// <item>Department target: the caller is a member of the target department
/// AND holds the requirement's narrowing role when one is named, otherwise
/// one of <c>TicketApprovalAppService.DepartmentTargetDefaultApproverRoles</c>.</item>
/// <item><see cref="Override"/>: the ADR-0024 <see cref="AuthorizationOverride"/>
/// applies, so every Pending approval is actionable — exactly as
/// <c>AuthorizationGate</c> would answer per record.</item>
/// </list>
/// Used by the Dashboard's Pending Approval KPI and the ticket queue's
/// <c>pendingApproval</c> drill-down filter, so the number and the list
/// always agree.
/// </summary>
/// <param name="CallerEmployeeId">The caller.</param>
/// <param name="CallerRoles">The caller's roles, from validated claims.</param>
/// <param name="MemberDepartmentIds">The caller's own department memberships.</param>
/// <param name="HoldsDepartmentTargetDefaultApproverRole">Whether the caller holds one of the default department-target approver roles.</param>
/// <param name="Override">Whether the authorization override applies to the caller.</param>
public sealed record ApprovalApproverScope(
    Guid CallerEmployeeId,
    IReadOnlyCollection<string> CallerRoles,
    IReadOnlyCollection<int> MemberDepartmentIds,
    bool HoldsDepartmentTargetDefaultApproverRole,
    bool Override)
{
    /// <summary>
    /// Builds the scope from the caller's roles and memberships.
    /// <paramref name="departmentTargetDefaultApproverRoles"/> is passed in
    /// (from <c>TicketApprovalAppService.DepartmentTargetDefaultApproverRoles</c>)
    /// rather than referenced here, so this record stays a plain value and
    /// the approval service remains the single owner of that role list.
    /// </summary>
    public static ApprovalApproverScope Resolve(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        IEnumerable<int> memberDepartmentIds,
        IReadOnlyCollection<string> departmentTargetDefaultApproverRoles) =>
        new(
            callerEmployeeId,
            callerRoles,
            memberDepartmentIds.Distinct().ToList(),
            callerRoles.Any(departmentTargetDefaultApproverRoles.Contains),
            AuthorizationOverride.AppliesTo(callerRoles));
}

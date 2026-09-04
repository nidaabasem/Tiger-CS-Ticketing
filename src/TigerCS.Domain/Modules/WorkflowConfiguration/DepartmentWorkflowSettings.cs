using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// Per-department workflow configuration (Workflow/SLA Configuration
/// phase 1) — one optional row per existing <see cref="Department"/>; the
/// Department entity itself is deliberately untouched. Identity data
/// (department id/name/active) stays on Department; employee directory data
/// is never duplicated here — the responsible authority is a role name from
/// the existing fixed role set, resolved through the existing
/// department-membership model at runtime.
///
/// <para>
/// The three capability flags configure the phase-2 assignment model
/// (CS assigns to department → department head assigns to employee) without
/// hard-coding it: today's role-set authorization
/// (<c>TicketRoleSets</c>) remains the enforcement point, and these flags
/// can only narrow behavior per department, mirroring how
/// <see cref="RequestType"/> flags narrow a <see cref="WorkflowTemplate"/>.
/// </para>
/// </summary>
public class DepartmentWorkflowSettings
{
    /// <summary>Also the primary key — at most one settings row per department.</summary>
    public int DepartmentId { get; private set; }

    /// <summary>Whether tickets in this department may be assigned to employees at all.</summary>
    public bool AllowAssignment { get; private set; }

    /// <summary>Whether an already-assigned ticket may be reassigned within the department.</summary>
    public bool AllowInternalReassignment { get; private set; }

    /// <summary>Whether tickets may be transferred out of this department to another.</summary>
    public bool AllowTransferToOtherDepartments { get; private set; }

    /// <summary>
    /// The role that acts as this department's responsible head for
    /// assignment/escalation purposes — a name from the existing fixed
    /// <see cref="Roles"/> set (default <see cref="Roles.DepartmentHead"/>),
    /// never an employee id: people are resolved through the existing
    /// role + department-membership model.
    /// </summary>
    public string HeadRoleName { get; private set; } = Roles.DepartmentHead;

    /// <summary>
    /// The role acting as this department's operational supervisor — the
    /// level between agent/team and department head in the Agent → Supervisor
    /// → Head → higher-authority escalation ladder. A name from the existing
    /// fixed <see cref="Roles"/> set; the fixed set has no dedicated
    /// "Department Supervisor" role, so the provisional default is
    /// <see cref="Roles.DepartmentHead"/> (the department-scoped supervisory
    /// authority that exists today) until the business decides whether a
    /// distinct supervisor role is introduced. Supervisor <i>visibility</i>
    /// stays governed by the existing department-scoped authorization —
    /// this configuration never widens it.
    /// </summary>
    public string SupervisorRoleName { get; private set; } = Roles.DepartmentHead;

    private DepartmentWorkflowSettings() { }

    public DepartmentWorkflowSettings(
        int departmentId,
        bool allowAssignment,
        bool allowInternalReassignment,
        bool allowTransferToOtherDepartments,
        string? headRoleName = null,
        string? supervisorRoleName = null)
    {
        DepartmentId = departmentId;
        AllowAssignment = allowAssignment;
        AllowInternalReassignment = allowInternalReassignment;
        AllowTransferToOtherDepartments = allowTransferToOtherDepartments;
        HeadRoleName = ValidateRole(headRoleName ?? Roles.DepartmentHead, nameof(headRoleName));
        SupervisorRoleName = ValidateRole(supervisorRoleName ?? Roles.DepartmentHead, nameof(supervisorRoleName));
    }

    private static string ValidateRole(string roleName, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(roleName))
        {
            throw new ArgumentException("A role name is required.", parameterName);
        }

        if (!Roles.All.Contains(roleName))
        {
            throw new ArgumentException($"Role '{roleName}' is not one of the fixed roles.", parameterName);
        }

        return roleName;
    }
}

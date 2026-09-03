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

    private DepartmentWorkflowSettings() { }

    public DepartmentWorkflowSettings(
        int departmentId,
        bool allowAssignment,
        bool allowInternalReassignment,
        bool allowTransferToOtherDepartments,
        string? headRoleName = null)
    {
        var resolvedHeadRole = headRoleName ?? Roles.DepartmentHead;
        if (string.IsNullOrWhiteSpace(resolvedHeadRole))
        {
            throw new ArgumentException("HeadRoleName is required.", nameof(headRoleName));
        }

        if (!Roles.All.Contains(resolvedHeadRole))
        {
            throw new ArgumentException(
                $"HeadRoleName '{resolvedHeadRole}' is not one of the fixed roles.", nameof(headRoleName));
        }

        DepartmentId = departmentId;
        AllowAssignment = allowAssignment;
        AllowInternalReassignment = allowInternalReassignment;
        AllowTransferToOtherDepartments = allowTransferToOtherDepartments;
        HeadRoleName = resolvedHeadRole;
    }
}

using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// The configured approval requirement of one request type (Workflow/
/// Automation phase 3) — configuration, never
/// <c>if (RequestTypeName == "Send Receipts")</c> logic. At most one
/// requirement per (request type, approval type); the seeded pair mirrors
/// the SLA document exactly: Collections / Send Receipts requires
/// <see cref="ApprovalType.AccountingApproval"/>, Handover Request requires
/// <see cref="ApprovalType.CustomerServiceApproval"/>.
///
/// <para>
/// The target (<see cref="TargetKind"/> + the nullable columns) says who
/// may decide, resolved through the existing identity model at decision
/// time — department membership, role holding, or one configured employee.
/// Accounting's provisional status is preserved: today its requirement
/// points at the provisionally seeded Accounting department, and moving to
/// an approval role or an external provider later is a configuration edit
/// (or an additive target kind), never a destructive redesign.
/// </para>
/// </summary>
public class RequestTypeApprovalRequirement
{
    public int RequestTypeApprovalRequirementId { get; private set; }
    public int RequestTypeId { get; private set; }
    public ApprovalType ApprovalType { get; private set; }

    public ApprovalTargetKind TargetKind { get; private set; }

    /// <summary>The deciding department for <see cref="ApprovalTargetKind.Department"/>; null otherwise.</summary>
    public int? TargetDepartmentId { get; private set; }

    /// <summary>
    /// For <see cref="ApprovalTargetKind.Role"/>: the deciding role. For
    /// <see cref="ApprovalTargetKind.Department"/>: an optional narrowing —
    /// the member must also hold this role; null means the provisional
    /// department-side default applies (see the approval service's
    /// remarks). Always a name from the fixed <see cref="Roles"/> set.
    /// </summary>
    public string? TargetRoleName { get; private set; }

    /// <summary>The one configured decider for <see cref="ApprovalTargetKind.Employee"/>; null otherwise.</summary>
    public Guid? TargetEmployeeId { get; private set; }

    /// <summary>Whether operational work is expected to wait for the decision (both seeded requirements: true, per the SLA document's sequencing). Informational for the workflow/UI in this phase — SLA consequences are phase 4.</summary>
    public bool BlocksWorkUntilApproved { get; private set; }

    public bool IsActive { get; private set; }

    private RequestTypeApprovalRequirement() { }

    private RequestTypeApprovalRequirement(
        int requestTypeId, ApprovalType approvalType, ApprovalTargetKind targetKind,
        int? targetDepartmentId, string? targetRoleName, Guid? targetEmployeeId,
        bool blocksWorkUntilApproved, bool isActive)
    {
        if (!Enum.IsDefined(approvalType))
        {
            throw new ArgumentException($"ApprovalType {approvalType} is not a defined approval type.", nameof(approvalType));
        }

        if (targetRoleName is not null && !Roles.All.Contains(targetRoleName))
        {
            throw new ArgumentException($"Role '{targetRoleName}' is not one of the fixed roles.", nameof(targetRoleName));
        }

        RequestTypeId = requestTypeId;
        ApprovalType = approvalType;
        TargetKind = targetKind;
        TargetDepartmentId = targetDepartmentId;
        TargetRoleName = targetRoleName;
        TargetEmployeeId = targetEmployeeId;
        BlocksWorkUntilApproved = blocksWorkUntilApproved;
        IsActive = isActive;
    }

    public static RequestTypeApprovalRequirement ForDepartment(
        int requestTypeId, ApprovalType approvalType, int targetDepartmentId,
        string? narrowedToRoleName = null, bool blocksWorkUntilApproved = true, bool isActive = true) =>
        new(requestTypeId, approvalType, ApprovalTargetKind.Department,
            targetDepartmentId, narrowedToRoleName, targetEmployeeId: null, blocksWorkUntilApproved, isActive);

    public static RequestTypeApprovalRequirement ForRole(
        int requestTypeId, ApprovalType approvalType, string targetRoleName,
        bool blocksWorkUntilApproved = true, bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(targetRoleName))
        {
            throw new ArgumentException("TargetRoleName is required for a role-targeted requirement.", nameof(targetRoleName));
        }

        return new(requestTypeId, approvalType, ApprovalTargetKind.Role,
            targetDepartmentId: null, targetRoleName, targetEmployeeId: null, blocksWorkUntilApproved, isActive);
    }

    public static RequestTypeApprovalRequirement ForEmployee(
        int requestTypeId, ApprovalType approvalType, Guid targetEmployeeId,
        bool blocksWorkUntilApproved = true, bool isActive = true)
    {
        if (targetEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("TargetEmployeeId is required for an employee-targeted requirement.", nameof(targetEmployeeId));
        }

        return new(requestTypeId, approvalType, ApprovalTargetKind.Employee,
            targetDepartmentId: null, targetRoleName: null, targetEmployeeId, blocksWorkUntilApproved, isActive);
    }
}

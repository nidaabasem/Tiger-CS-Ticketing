using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;

/// <summary>Reads the seeded reusable workflow templates (Workflow/SLA Configuration phase 1). Configuration data — read-only for this module's services.</summary>
public interface IWorkflowTemplateRepository
{
    Task<WorkflowTemplate?> GetByIdAsync(int workflowTemplateId, CancellationToken cancellationToken = default);

    Task<WorkflowTemplate?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);
}

/// <summary>Reads the per-department request type configuration.</summary>
public interface IRequestTypeRepository
{
    Task<RequestType?> GetByIdAsync(int requestTypeId, CancellationToken cancellationToken = default);

    /// <summary>Active request types of one department, ordered by name — the set an agent can pick from when raising a request for that department.</summary>
    Task<IReadOnlyList<RequestType>> ListActiveByDepartmentAsync(int departmentId, CancellationToken cancellationToken = default);
}

/// <summary>Reads the per-(request type, priority) SLA configuration.</summary>
public interface IRequestTypeSlaPolicyRepository
{
    /// <summary>The active SLA row for this exact (request type, priority) pair, or null when none is configured — the caller decides the fallback, never this repository.</summary>
    Task<RequestTypeSlaPolicy?> GetActiveAsync(int requestTypeId, byte priorityId, CancellationToken cancellationToken = default);

    /// <summary>All SLA rows of one request type (active and inactive), ordered by priority.</summary>
    Task<IReadOnlyList<RequestTypeSlaPolicy>> ListByRequestTypeAsync(int requestTypeId, CancellationToken cancellationToken = default);
}

/// <summary>Reads the optional per-department workflow settings row.</summary>
public interface IDepartmentWorkflowSettingsRepository
{
    Task<DepartmentWorkflowSettings?> GetByDepartmentIdAsync(int departmentId, CancellationToken cancellationToken = default);
}

/// <summary>Reads the per-request-type assignment rule (Workflow/Automation phase 2). At most one rule per request type; absence means department queue.</summary>
public interface IRequestTypeAssignmentRuleRepository
{
    Task<RequestTypeAssignmentRule?> GetByRequestTypeIdAsync(int requestTypeId, CancellationToken cancellationToken = default);
}

/// <summary>Reads the per-request-type approval requirements (Workflow/Automation phase 3). At most one per (request type, approval type); absence means no approval is required.</summary>
public interface IRequestTypeApprovalRequirementRepository
{
    Task<IReadOnlyList<RequestTypeApprovalRequirement>> ListActiveByRequestTypeIdAsync(
        int requestTypeId, CancellationToken cancellationToken = default);

    Task<RequestTypeApprovalRequirement?> GetActiveAsync(
        int requestTypeId, ApprovalType approvalType, CancellationToken cancellationToken = default);
}

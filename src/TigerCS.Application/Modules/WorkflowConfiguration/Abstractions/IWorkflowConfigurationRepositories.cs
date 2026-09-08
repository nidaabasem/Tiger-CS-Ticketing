using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;

/// <summary>The logical workflows (Administration / Workflow Designer phase).</summary>
public interface IWorkflowRepository
{
    Task<Workflow?> GetByIdAsync(int workflowId, CancellationToken cancellationToken = default);

    Task<Workflow?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Workflow>> ListAsync(bool includeInactive, CancellationToken cancellationToken = default);

    Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken = default);

    Task AddAsync(Workflow workflow, CancellationToken cancellationToken = default);
}

/// <summary>Workflow VERSIONS — the phase-1 template rows, now versioned.</summary>
public interface IWorkflowTemplateRepository
{
    Task<WorkflowTemplate?> GetByIdAsync(int workflowTemplateId, CancellationToken cancellationToken = default);

    Task<WorkflowTemplate?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowTemplate>> ListByWorkflowIdAsync(int workflowId, CancellationToken cancellationToken = default);

    /// <summary>The single Published version of a workflow, or null when none is published (a workflow whose only version is still a Draft).</summary>
    Task<WorkflowTemplate?> GetPublishedAsync(int workflowId, CancellationToken cancellationToken = default);

    /// <summary>The single Draft of a workflow, or null when there is none.</summary>
    Task<WorkflowTemplate?> GetDraftAsync(int workflowId, CancellationToken cancellationToken = default);

    Task AddAsync(WorkflowTemplate version, CancellationToken cancellationToken = default);

    /// <summary>Physically removes a version — the application service only ever calls this for an unreferenced Draft.</summary>
    void Remove(WorkflowTemplate version);

    /// <summary>How many tickets are pinned to this exact version — the reference count that makes a version undeletable.</summary>
    Task<int> CountPinnedTicketsAsync(int workflowTemplateId, CancellationToken cancellationToken = default);

    /// <summary>Pinned-ticket counts per version of one workflow (versions with no tickets are absent).</summary>
    Task<IReadOnlyDictionary<int, int>> CountPinnedTicketsByVersionAsync(int workflowId, CancellationToken cancellationToken = default);
}

public interface IRequestTypeRepository
{
    Task<RequestType?> GetByIdAsync(int requestTypeId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RequestType>> ListActiveByDepartmentAsync(int departmentId, CancellationToken cancellationToken = default);

    /// <summary>Administration listing — optionally one department, optionally including inactive rows.</summary>
    Task<IReadOnlyList<RequestType>> ListAsync(int? departmentId, bool includeInactive, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RequestType>> ListByWorkflowIdAsync(int workflowId, CancellationToken cancellationToken = default);

    Task<bool> NameExistsAsync(int departmentId, string name, int? excludeRequestTypeId, CancellationToken cancellationToken = default);

    Task AddAsync(RequestType requestType, CancellationToken cancellationToken = default);

    /// <summary>How many tickets reference this request type — the count that makes it undeletable and its department immutable.</summary>
    Task<int> CountTicketsAsync(int requestTypeId, CancellationToken cancellationToken = default);
}

public interface IRequestTypeSlaPolicyRepository
{
    Task<RequestTypeSlaPolicy?> GetActiveAsync(int requestTypeId, byte priorityId, CancellationToken cancellationToken = default);

    /// <summary>The row for a (request type, priority) pair regardless of its active flag — administration edits target it.</summary>
    Task<RequestTypeSlaPolicy?> GetAsync(int requestTypeId, byte priorityId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RequestTypeSlaPolicy>> ListByRequestTypeAsync(int requestTypeId, CancellationToken cancellationToken = default);

    Task AddAsync(RequestTypeSlaPolicy policy, CancellationToken cancellationToken = default);
}

public interface IDepartmentWorkflowSettingsRepository
{
    Task<DepartmentWorkflowSettings?> GetByDepartmentIdAsync(int departmentId, CancellationToken cancellationToken = default);
}

public interface IRequestTypeAssignmentRuleRepository
{
    Task<RequestTypeAssignmentRule?> GetByRequestTypeIdAsync(int requestTypeId, CancellationToken cancellationToken = default);

    Task AddAsync(RequestTypeAssignmentRule rule, CancellationToken cancellationToken = default);

    /// <summary>Removes a configuration row so it can be replaced (rules are immutable value configuration; a change is a new rule).</summary>
    void Remove(RequestTypeAssignmentRule rule);
}

public interface IRequestTypeApprovalRequirementRepository
{
    Task<IReadOnlyList<RequestTypeApprovalRequirement>> ListActiveByRequestTypeIdAsync(
        int requestTypeId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RequestTypeApprovalRequirement>> ListByRequestTypeIdAsync(
        int requestTypeId, CancellationToken cancellationToken = default);

    Task<RequestTypeApprovalRequirement?> GetActiveAsync(
        int requestTypeId, ApprovalType approvalType, CancellationToken cancellationToken = default);

    /// <summary>The row for a (request type, approval type) pair regardless of its active flag.</summary>
    Task<RequestTypeApprovalRequirement?> GetAsync(
        int requestTypeId, ApprovalType approvalType, CancellationToken cancellationToken = default);

    Task AddAsync(RequestTypeApprovalRequirement requirement, CancellationToken cancellationToken = default);
}

/// <summary>Commits administration edits of workflow configuration.</summary>
public interface IWorkflowConfigurationUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

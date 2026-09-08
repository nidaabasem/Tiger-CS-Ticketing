using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Repositories;

public sealed class WorkflowRepository(TigerCsDbContext dbContext) : IWorkflowRepository
{
    public Task<Workflow?> GetByIdAsync(int workflowId, CancellationToken cancellationToken = default) =>
        dbContext.Workflows.FirstOrDefaultAsync(w => w.WorkflowId == workflowId, cancellationToken);

    public Task<Workflow?> GetByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        dbContext.Workflows.FirstOrDefaultAsync(w => w.Code == code, cancellationToken);

    public async Task<IReadOnlyList<Workflow>> ListAsync(bool includeInactive, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Workflows.AsQueryable();
        if (!includeInactive)
        {
            query = query.Where(w => w.IsActive);
        }

        return await query.OrderBy(w => w.Name).ToListAsync(cancellationToken);
    }

    public Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken = default) =>
        dbContext.Workflows.AnyAsync(w => w.Code == code, cancellationToken);

    public async Task AddAsync(Workflow workflow, CancellationToken cancellationToken = default) =>
        await dbContext.Workflows.AddAsync(workflow, cancellationToken);
}

public sealed class WorkflowTemplateRepository(TigerCsDbContext dbContext) : IWorkflowTemplateRepository
{
    // Steps auto-include; each step's transitions auto-include too, and the
    // target-step navigation resolves through the identity map because every
    // target belongs to the same (already loaded) version.
    public Task<WorkflowTemplate?> GetByIdAsync(int workflowTemplateId, CancellationToken cancellationToken = default) =>
        dbContext.WorkflowTemplates.FirstOrDefaultAsync(t => t.WorkflowTemplateId == workflowTemplateId, cancellationToken);

    public Task<WorkflowTemplate?> GetByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        dbContext.WorkflowTemplates.FirstOrDefaultAsync(t => t.Code == code, cancellationToken);

    public async Task<IReadOnlyList<WorkflowTemplate>> ListByWorkflowIdAsync(int workflowId, CancellationToken cancellationToken = default) =>
        await dbContext.WorkflowTemplates
            .Where(t => t.WorkflowId == workflowId)
            .OrderBy(t => t.VersionNumber)
            .ToListAsync(cancellationToken);

    public Task<WorkflowTemplate?> GetPublishedAsync(int workflowId, CancellationToken cancellationToken = default) =>
        dbContext.WorkflowTemplates.FirstOrDefaultAsync(
            t => t.WorkflowId == workflowId && t.Status == WorkflowVersionStatus.Published, cancellationToken);

    public Task<WorkflowTemplate?> GetDraftAsync(int workflowId, CancellationToken cancellationToken = default) =>
        dbContext.WorkflowTemplates.FirstOrDefaultAsync(
            t => t.WorkflowId == workflowId && t.Status == WorkflowVersionStatus.Draft, cancellationToken);

    public async Task AddAsync(WorkflowTemplate version, CancellationToken cancellationToken = default) =>
        await dbContext.WorkflowTemplates.AddAsync(version, cancellationToken);

    public void Remove(WorkflowTemplate version)
    {
        // Only ever an unreferenced Draft (the service guarantees that).
        // Branches are removed first: the target-step relationship is
        // Restrict, so letting the cascade from the source step race the
        // target step's own deletion would sever a required relationship.
        dbContext.WorkflowStepTransitions.RemoveRange(version.Steps.SelectMany(s => s.Transitions));
        dbContext.WorkflowTemplateSteps.RemoveRange(version.Steps);
        dbContext.WorkflowTemplates.Remove(version);
    }

    public Task<int> CountPinnedTicketsAsync(int workflowTemplateId, CancellationToken cancellationToken = default) =>
        dbContext.Tickets.CountAsync(t => t.WorkflowTemplateId == workflowTemplateId, cancellationToken);

    public async Task<IReadOnlyDictionary<int, int>> CountPinnedTicketsByVersionAsync(int workflowId, CancellationToken cancellationToken = default)
    {
        var versionIds = dbContext.WorkflowTemplates
            .Where(t => t.WorkflowId == workflowId)
            .Select(t => t.WorkflowTemplateId);

        var counts = await dbContext.Tickets
            .Where(t => t.WorkflowTemplateId != null && versionIds.Contains(t.WorkflowTemplateId.Value))
            .GroupBy(t => t.WorkflowTemplateId!.Value)
            .Select(g => new { VersionId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(c => c.VersionId, c => c.Count);
    }
}

public sealed class RequestTypeRepository(TigerCsDbContext dbContext) : IRequestTypeRepository
{
    public Task<RequestType?> GetByIdAsync(int requestTypeId, CancellationToken cancellationToken = default) =>
        dbContext.RequestTypes.FirstOrDefaultAsync(r => r.RequestTypeId == requestTypeId, cancellationToken);

    public async Task<IReadOnlyList<RequestType>> ListActiveByDepartmentAsync(
        int departmentId, CancellationToken cancellationToken = default) =>
        await dbContext.RequestTypes
            .Where(r => r.DepartmentId == departmentId && r.IsActive)
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RequestType>> ListAsync(int? departmentId, bool includeInactive, CancellationToken cancellationToken = default)
    {
        var query = dbContext.RequestTypes.AsQueryable();
        if (departmentId is { } id)
        {
            query = query.Where(r => r.DepartmentId == id);
        }

        if (!includeInactive)
        {
            query = query.Where(r => r.IsActive);
        }

        return await query.OrderBy(r => r.DepartmentId).ThenBy(r => r.Name).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RequestType>> ListByWorkflowIdAsync(int workflowId, CancellationToken cancellationToken = default) =>
        await dbContext.RequestTypes.Where(r => r.WorkflowId == workflowId).OrderBy(r => r.Name).ToListAsync(cancellationToken);

    public Task<bool> NameExistsAsync(int departmentId, string name, int? excludeRequestTypeId, CancellationToken cancellationToken = default) =>
        dbContext.RequestTypes.AnyAsync(
            r => r.DepartmentId == departmentId && r.Name == name
                && (excludeRequestTypeId == null || r.RequestTypeId != excludeRequestTypeId),
            cancellationToken);

    public async Task AddAsync(RequestType requestType, CancellationToken cancellationToken = default) =>
        await dbContext.RequestTypes.AddAsync(requestType, cancellationToken);

    public Task<int> CountTicketsAsync(int requestTypeId, CancellationToken cancellationToken = default) =>
        dbContext.Tickets.CountAsync(t => t.RequestTypeId == requestTypeId, cancellationToken);
}

public sealed class RequestTypeSlaPolicyRepository(TigerCsDbContext dbContext) : IRequestTypeSlaPolicyRepository
{
    public Task<RequestTypeSlaPolicy?> GetActiveAsync(
        int requestTypeId, byte priorityId, CancellationToken cancellationToken = default) =>
        dbContext.RequestTypeSlaPolicies.FirstOrDefaultAsync(
            p => p.RequestTypeId == requestTypeId && p.PriorityId == priorityId && p.IsActive, cancellationToken);

    public Task<RequestTypeSlaPolicy?> GetAsync(
        int requestTypeId, byte priorityId, CancellationToken cancellationToken = default) =>
        dbContext.RequestTypeSlaPolicies.FirstOrDefaultAsync(
            p => p.RequestTypeId == requestTypeId && p.PriorityId == priorityId, cancellationToken);

    public async Task<IReadOnlyList<RequestTypeSlaPolicy>> ListByRequestTypeAsync(
        int requestTypeId, CancellationToken cancellationToken = default) =>
        await dbContext.RequestTypeSlaPolicies
            .Where(p => p.RequestTypeId == requestTypeId)
            .OrderBy(p => p.PriorityId)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(RequestTypeSlaPolicy policy, CancellationToken cancellationToken = default) =>
        await dbContext.RequestTypeSlaPolicies.AddAsync(policy, cancellationToken);
}

public sealed class DepartmentWorkflowSettingsRepository(TigerCsDbContext dbContext) : IDepartmentWorkflowSettingsRepository
{
    public Task<DepartmentWorkflowSettings?> GetByDepartmentIdAsync(
        int departmentId, CancellationToken cancellationToken = default) =>
        dbContext.DepartmentWorkflowSettings.FirstOrDefaultAsync(s => s.DepartmentId == departmentId, cancellationToken);
}

public sealed class RequestTypeAssignmentRuleRepository(TigerCsDbContext dbContext) : IRequestTypeAssignmentRuleRepository
{
    public Task<RequestTypeAssignmentRule?> GetByRequestTypeIdAsync(
        int requestTypeId, CancellationToken cancellationToken = default) =>
        dbContext.RequestTypeAssignmentRules.FirstOrDefaultAsync(r => r.RequestTypeId == requestTypeId, cancellationToken);

    public async Task AddAsync(RequestTypeAssignmentRule rule, CancellationToken cancellationToken = default) =>
        await dbContext.RequestTypeAssignmentRules.AddAsync(rule, cancellationToken);

    public void Remove(RequestTypeAssignmentRule rule) => dbContext.RequestTypeAssignmentRules.Remove(rule);
}

public sealed class RequestTypeApprovalRequirementRepository(TigerCsDbContext dbContext) : IRequestTypeApprovalRequirementRepository
{
    public async Task<IReadOnlyList<RequestTypeApprovalRequirement>> ListActiveByRequestTypeIdAsync(
        int requestTypeId, CancellationToken cancellationToken = default) =>
        await dbContext.RequestTypeApprovalRequirements
            .Where(r => r.RequestTypeId == requestTypeId && r.IsActive)
            .OrderBy(r => r.ApprovalType)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RequestTypeApprovalRequirement>> ListByRequestTypeIdAsync(
        int requestTypeId, CancellationToken cancellationToken = default) =>
        await dbContext.RequestTypeApprovalRequirements
            .Where(r => r.RequestTypeId == requestTypeId)
            .OrderBy(r => r.ApprovalType)
            .ToListAsync(cancellationToken);

    public Task<RequestTypeApprovalRequirement?> GetActiveAsync(
        int requestTypeId, ApprovalType approvalType, CancellationToken cancellationToken = default) =>
        dbContext.RequestTypeApprovalRequirements.FirstOrDefaultAsync(
            r => r.RequestTypeId == requestTypeId && r.ApprovalType == approvalType && r.IsActive, cancellationToken);

    public Task<RequestTypeApprovalRequirement?> GetAsync(
        int requestTypeId, ApprovalType approvalType, CancellationToken cancellationToken = default) =>
        dbContext.RequestTypeApprovalRequirements.FirstOrDefaultAsync(
            r => r.RequestTypeId == requestTypeId && r.ApprovalType == approvalType, cancellationToken);

    public async Task AddAsync(RequestTypeApprovalRequirement requirement, CancellationToken cancellationToken = default) =>
        await dbContext.RequestTypeApprovalRequirements.AddAsync(requirement, cancellationToken);
}

public sealed class WorkflowConfigurationUnitOfWork(TigerCsDbContext dbContext) : IWorkflowConfigurationUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => dbContext.SaveChangesAsync(cancellationToken);
}

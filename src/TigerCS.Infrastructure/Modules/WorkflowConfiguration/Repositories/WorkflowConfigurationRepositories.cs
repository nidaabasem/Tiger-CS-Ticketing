using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Repositories;

public sealed class WorkflowTemplateRepository(TigerCsDbContext dbContext) : IWorkflowTemplateRepository
{
    // Steps are AutoInclude'd by the template's configuration, so both reads
    // return the full flow.
    public Task<WorkflowTemplate?> GetByIdAsync(int workflowTemplateId, CancellationToken cancellationToken = default) =>
        dbContext.WorkflowTemplates.FirstOrDefaultAsync(t => t.WorkflowTemplateId == workflowTemplateId, cancellationToken);

    public Task<WorkflowTemplate?> GetByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        dbContext.WorkflowTemplates.FirstOrDefaultAsync(t => t.Code == code, cancellationToken);
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
}

public sealed class RequestTypeSlaPolicyRepository(TigerCsDbContext dbContext) : IRequestTypeSlaPolicyRepository
{
    public Task<RequestTypeSlaPolicy?> GetActiveAsync(
        int requestTypeId, byte priorityId, CancellationToken cancellationToken = default) =>
        dbContext.RequestTypeSlaPolicies.FirstOrDefaultAsync(
            p => p.RequestTypeId == requestTypeId && p.PriorityId == priorityId && p.IsActive, cancellationToken);

    public async Task<IReadOnlyList<RequestTypeSlaPolicy>> ListByRequestTypeAsync(
        int requestTypeId, CancellationToken cancellationToken = default) =>
        await dbContext.RequestTypeSlaPolicies
            .Where(p => p.RequestTypeId == requestTypeId)
            .OrderBy(p => p.PriorityId)
            .ToListAsync(cancellationToken);
}

public sealed class DepartmentWorkflowSettingsRepository(TigerCsDbContext dbContext) : IDepartmentWorkflowSettingsRepository
{
    public Task<DepartmentWorkflowSettings?> GetByDepartmentIdAsync(
        int departmentId, CancellationToken cancellationToken = default) =>
        dbContext.DepartmentWorkflowSettings.FirstOrDefaultAsync(s => s.DepartmentId == departmentId, cancellationToken);
}

public sealed class RequestTypeAssignmentRuleRepository(TigerCsDbContext dbContext) : IRequestTypeAssignmentRuleRepository
{
    // Members are AutoInclude'd by the rule's configuration.
    public Task<RequestTypeAssignmentRule?> GetByRequestTypeIdAsync(
        int requestTypeId, CancellationToken cancellationToken = default) =>
        dbContext.RequestTypeAssignmentRules.FirstOrDefaultAsync(r => r.RequestTypeId == requestTypeId, cancellationToken);
}

public sealed class RequestTypeApprovalRequirementRepository(TigerCsDbContext dbContext) : IRequestTypeApprovalRequirementRepository
{
    public async Task<IReadOnlyList<RequestTypeApprovalRequirement>> ListActiveByRequestTypeIdAsync(
        int requestTypeId, CancellationToken cancellationToken = default) =>
        await dbContext.RequestTypeApprovalRequirements
            .Where(r => r.RequestTypeId == requestTypeId && r.IsActive)
            .OrderBy(r => r.ApprovalType)
            .ToListAsync(cancellationToken);

    public Task<RequestTypeApprovalRequirement?> GetActiveAsync(
        int requestTypeId, ApprovalType approvalType, CancellationToken cancellationToken = default) =>
        dbContext.RequestTypeApprovalRequirements.FirstOrDefaultAsync(
            r => r.RequestTypeId == requestTypeId && r.ApprovalType == approvalType && r.IsActive, cancellationToken);
}

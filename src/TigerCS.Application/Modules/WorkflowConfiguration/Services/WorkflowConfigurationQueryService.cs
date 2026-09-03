using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Application.Modules.WorkflowConfiguration.Dto;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.WorkflowConfiguration.Services;

/// <summary>
/// Read side of the Workflow/SLA configuration layer (phase 1): which request
/// types a department offers, which workflow (template + steps + effective
/// capabilities) a request type follows, and which SLA row applies to a
/// (request type, priority) pair. Phase-2 transition enforcement and the
/// Ticket Details action list both consume these answers, so the rule lives
/// here once.
/// </summary>
public sealed class WorkflowConfigurationQueryService(
    IRequestTypeRepository requestTypeRepository,
    IWorkflowTemplateRepository workflowTemplateRepository,
    IRequestTypeSlaPolicyRepository slaPolicyRepository)
{
    public async Task<IReadOnlyList<RequestTypeSummaryDto>> ListRequestTypesForDepartmentAsync(
        int departmentId, CancellationToken cancellationToken = default)
    {
        var requestTypes = await requestTypeRepository.ListActiveByDepartmentAsync(departmentId, cancellationToken);

        var summaries = new List<RequestTypeSummaryDto>(requestTypes.Count);
        foreach (var requestType in requestTypes)
        {
            var template = await GetTemplateOrThrowAsync(requestType, cancellationToken);
            summaries.Add(new RequestTypeSummaryDto(
                requestType.RequestTypeId,
                requestType.Name,
                requestType.DepartmentId,
                template.Code,
                requestType.DefaultPriorityId,
                requestType.AllowAgentPriorityChange));
        }

        return summaries;
    }

    /// <summary>The workflow a request type follows, or null for an unknown/inactive request type.</summary>
    public async Task<RequestTypeWorkflowDto?> GetWorkflowAsync(int requestTypeId, CancellationToken cancellationToken = default)
    {
        var requestType = await requestTypeRepository.GetByIdAsync(requestTypeId, cancellationToken);
        if (requestType is null || !requestType.IsActive)
        {
            return null;
        }

        var template = await GetTemplateOrThrowAsync(requestType, cancellationToken);

        return new RequestTypeWorkflowDto(
            requestType.RequestTypeId,
            requestType.Name,
            template.Code,
            template.Name,
            template.Steps
                .OrderBy(s => s.Sequence)
                .Select(s => new WorkflowStepDto(s.Sequence, s.Name, s.Kind, s.IsOptional))
                .ToList(),
            WorkflowCapabilities.Resolve(template, requestType));
    }

    /// <summary>
    /// The active SLA row for this exact (request type, priority) pair, or
    /// null when none is configured. <b>No fallback is invented here</b>: how
    /// an unconfigured pair relates to the existing per-priority
    /// <c>SlaPolicy</c> defaults is a phase-4 calculation decision recorded
    /// in docs/Workflow-SLA-Configuration-Phase1.md, and callers must treat
    /// null as "not configured", not as "no SLA".
    /// </summary>
    public async Task<RequestTypeSlaDto?> ResolveSlaAsync(
        int requestTypeId, byte priorityId, CancellationToken cancellationToken = default)
    {
        var policy = await slaPolicyRepository.GetActiveAsync(requestTypeId, priorityId, cancellationToken);
        if (policy is null)
        {
            return null;
        }

        return new RequestTypeSlaDto(
            policy.RequestTypeId,
            policy.PriorityId,
            policy.Trigger,
            policy.Unit,
            policy.FirstResponseTargetValue,
            policy.FirstResponseMaximumValue,
            policy.ResolutionTargetValue,
            policy.ResolutionMaximumValue,
            policy.IsImmediate,
            policy.PausesOnPendingCustomer,
            policy.PausesOnPendingInternal);
    }

    private async Task<WorkflowTemplate> GetTemplateOrThrowAsync(RequestType requestType, CancellationToken cancellationToken)
    {
        var template = await workflowTemplateRepository.GetByIdAsync(requestType.WorkflowTemplateId, cancellationToken);

        // A request type referencing a missing template is broken seed/config
        // data, not a normal outcome — surfaced loudly rather than skipped.
        return template ?? throw new InvalidOperationException(
            $"RequestType {requestType.RequestTypeId} references WorkflowTemplate {requestType.WorkflowTemplateId}, which does not exist.");
    }
}

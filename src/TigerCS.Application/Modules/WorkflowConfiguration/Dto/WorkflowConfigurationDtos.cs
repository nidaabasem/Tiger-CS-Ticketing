using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.WorkflowConfiguration.Dto;

/// <summary>One request type as offered for selection within its department.</summary>
public sealed record RequestTypeSummaryDto(
    int RequestTypeId,
    string Name,
    int DepartmentId,
    string WorkflowTemplateCode,
    byte DefaultPriorityId,
    bool AllowAgentPriorityChange);

/// <summary>One displayable workflow step.</summary>
public sealed record WorkflowStepDto(byte Sequence, string Name, WorkflowStepKind Kind, bool IsOptional);

/// <summary>A request type's resolved workflow: its template, the displayable step flow, and the effective capabilities (template ∧ request type).</summary>
public sealed record RequestTypeWorkflowDto(
    int RequestTypeId,
    string RequestTypeName,
    string WorkflowTemplateCode,
    string WorkflowTemplateName,
    IReadOnlyList<WorkflowStepDto> Steps,
    WorkflowCapabilities Capabilities);

/// <summary>
/// One resolved SLA configuration row. Duration values are verbatim source
/// configuration in <see cref="Unit"/> — a range keeps both bounds, an
/// "Immediately" entry carries <see cref="IsImmediate"/>, and the pending
/// business decisions (pause behavior, clock basis) surface as nulls rather
/// than silently-defaulted values.
/// </summary>
public sealed record RequestTypeSlaDto(
    int RequestTypeId,
    byte PriorityId,
    SlaTriggerType Trigger,
    SlaDurationUnit Unit,
    int? FirstResponseTargetValue,
    int? FirstResponseMaximumValue,
    int? ResolutionTargetValue,
    int? ResolutionMaximumValue,
    bool IsImmediate,
    bool? PausesOnPendingCustomer,
    bool? PausesOnPendingInternal);

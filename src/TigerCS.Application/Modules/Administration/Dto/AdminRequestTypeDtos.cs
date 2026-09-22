using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.Administration.Dto;

public sealed record AdminRequestTypeSummaryDto(
    int RequestTypeId,
    string Name,
    int DepartmentId,
    string DepartmentName,
    bool IsActive,
    int WorkflowId,
    string WorkflowName,
    int? ActiveVersionNumber,
    string AssignmentSummary,
    byte DefaultPriorityId,
    int TicketCount);

public sealed record AssignmentRuleDto(
    AssignmentMode Mode,
    Guid? PrimaryEmployeeId,
    string? PrimaryEmployeeName,
    string? TeamName,
    IReadOnlyList<NamedEmployeeDto> Members,
    bool IsActive);

public sealed record ApprovalRequirementDto(
    ApprovalType ApprovalType,
    string ApprovalTypeLabel,
    ApprovalTargetKind TargetKind,
    int? TargetDepartmentId,
    string? TargetDepartmentName,
    string? TargetRoleName,
    Guid? TargetEmployeeId,
    string? TargetEmployeeName,
    bool BlocksWorkUntilApproved,
    bool IsActive);

/// <summary>
/// One request type's SLA policy for one priority.
///
/// <para>
/// <c>PausesOnPendingInternal</c> reports the stored value of the retired
/// Pending Internal / Third Party pause setting. It is read-only in practice:
/// reported so historical configuration stays visible, but nothing writes it
/// any more.
/// </para>
/// </summary>
public sealed record SlaPolicyDto(
    byte PriorityId,
    SlaTriggerType Trigger,
    SlaDurationUnit Unit,
    int? FirstResponseTargetValue,
    int? FirstResponseMaximumValue,
    int? ResolutionTargetValue,
    int? ResolutionMaximumValue,
    bool IsImmediate,
    SlaClockBasis? ClockBasis,
    bool? PausesOnPendingCustomer,
    bool? PausesOnPendingInternal,
    decimal? WarningThresholdPercent,
    bool IsActive);

/// <summary>The request type's workflow link, resolved to the logical workflow and its current Published version.</summary>
public sealed record WorkflowLinkDto(
    int WorkflowId,
    string WorkflowName,
    bool WorkflowIsActive,
    int? ActiveVersionId,
    int? ActiveVersionNumber,
    int? DraftVersionId,
    int? DraftVersionNumber,
    IReadOnlyList<ApprovalType> ApprovalTypesInActiveVersion);

public sealed record AdminRequestTypeDetailDto(
    int RequestTypeId,
    string Name,
    int DepartmentId,
    string DepartmentName,
    bool IsActive,
    byte DefaultPriorityId,
    bool AllowAgentPriorityChange,
    bool AllowPendingCustomer,
    bool AllowPendingInternal,
    bool AllowReopen,
    string? RequiredFieldsJson,
    int TicketCount,
    WorkflowLinkDto Workflow,
    AssignmentRuleDto? AssignmentRule,
    IReadOnlyList<ApprovalRequirementDto> ApprovalRequirements,
    IReadOnlyList<SlaPolicyDto> SlaPolicies);

public sealed record SaveRequestTypeRequestDto(
    int DepartmentId,
    string Name,
    int WorkflowId,
    byte DefaultPriorityId,
    bool AllowAgentPriorityChange,
    bool AllowPendingCustomer,
    bool AllowPendingInternal,
    bool AllowReopen,
    string? RequiredFieldsJson = null);

public sealed record SaveAssignmentRuleRequestDto(
    AssignmentMode Mode,
    Guid? PrimaryEmployeeId,
    IReadOnlyList<Guid>? MemberEmployeeIds,
    string? TeamName,
    bool IsActive = true);

public sealed record SaveApprovalRequirementRequestDto(
    ApprovalTargetKind TargetKind,
    int? TargetDepartmentId,
    string? TargetRoleName,
    Guid? TargetEmployeeId,
    bool BlocksWorkUntilApproved = true,
    bool IsActive = true);

/// <summary>
/// Create or update one request type's SLA policy for one priority.
///
/// <para>
/// <b><c>PausesOnPendingInternal</c> is deprecated — accepted for wire
/// compatibility, never applied.</b> It configured the pause window for
/// <c>TicketStatus.PendingThirdParty</c>, which the approved lifecycle
/// cleanup retired. A new policy is created "not decided" and an existing one
/// keeps whatever it already stores, so historical configuration is preserved
/// and the column needs no migration.
/// </para>
/// </summary>
public sealed record SaveSlaPolicyRequestDto(
    SlaTriggerType Trigger,
    SlaDurationUnit Unit,
    int? FirstResponseTargetValue,
    int? FirstResponseMaximumValue,
    int? ResolutionTargetValue,
    int? ResolutionMaximumValue,
    bool IsImmediate,
    SlaClockBasis? ClockBasis,
    bool? PausesOnPendingCustomer,
    bool? PausesOnPendingInternal,
    decimal? WarningThresholdPercent,
    bool IsActive = true);

/// <summary>What the New Ticket picker needs: the active request types of one department, by name.</summary>
public sealed record RequestTypeOptionDto(int RequestTypeId, string Name, int DepartmentId, byte DefaultPriorityId, bool HasPublishedWorkflow);

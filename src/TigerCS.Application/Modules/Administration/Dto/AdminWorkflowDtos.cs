using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.Administration.Dto;

public sealed record AdminWorkflowSummaryDto(
    int WorkflowId,
    string Name,
    string? Description,
    bool IsActive,
    int? ActiveVersionId,
    int? ActiveVersionNumber,
    int? DraftVersionId,
    int? DraftVersionNumber,
    int VersionCount,
    int RequestTypeCount,
    int TicketCount);

public sealed record WorkflowVersionSummaryDto(
    int WorkflowTemplateId,
    int VersionNumber,
    WorkflowVersionStatus Status,
    string Name,
    DateTime CreatedAtUtc,
    string? CreatedByName,
    DateTime? PublishedAtUtc,
    string? PublishedByName,
    int StepCount,
    int TicketCount,
    bool CanDelete);

public sealed record WorkflowRequestTypeUsageDto(int RequestTypeId, string Name, string DepartmentName, bool IsActive);

public sealed record AdminWorkflowDetailDto(
    int WorkflowId,
    string Name,
    string? Description,
    bool IsActive,
    DateTime CreatedAtUtc,
    IReadOnlyList<WorkflowVersionSummaryDto> Versions,
    IReadOnlyList<WorkflowRequestTypeUsageDto> RequestTypes);

public sealed record StepTransitionDto(WorkflowStepOutcome Outcome, int TargetStepId, byte TargetSequence, string TargetName);

public sealed record WorkflowVersionStepDto(
    int WorkflowTemplateStepId,
    byte Sequence,
    string Name,
    WorkflowStepKind Kind,
    string KindLabel,
    bool IsOptional,
    ApprovalType? ApprovalType,
    string? ApprovalTypeLabel,
    bool RequiresApprovalType,
    bool SupportsOutcomeBranches,
    IReadOnlyList<StepTransitionDto> Transitions);

public sealed record WorkflowValidationIssueDto(WorkflowValidationSeverity Severity, byte? StepSequence, string Message);

public sealed record WorkflowVersionDetailDto(
    int WorkflowTemplateId,
    int WorkflowId,
    string WorkflowName,
    int VersionNumber,
    WorkflowVersionStatus Status,
    string Name,
    string? Description,
    bool AllowsPendingCustomer,
    bool AllowsPendingInternal,
    bool RequiresApproval,
    DateTime CreatedAtUtc,
    string? CreatedByName,
    DateTime? PublishedAtUtc,
    string? PublishedByName,
    int TicketCount,
    bool IsEditable,
    bool CanPublish,
    bool CanDelete,
    IReadOnlyList<WorkflowVersionStepDto> Steps,
    IReadOnlyList<WorkflowValidationIssueDto> Validation);

public sealed record WorkflowStepKindDto(
    WorkflowStepKind Kind, string Label, string Description, bool RequiresApprovalType, bool SupportsOutcomeBranches, bool IsStart, bool IsTerminal);

public sealed record ApprovalTypeOptionDto(ApprovalType ApprovalType, string Label);

/// <summary>Everything the designer's forms need to offer only controlled choices.</summary>
public sealed record WorkflowDesignerCatalogDto(
    IReadOnlyList<WorkflowStepKindDto> StepKinds,
    IReadOnlyList<ApprovalTypeOptionDto> ApprovalTypes);

public sealed record CreateWorkflowRequestDto(string Name, string? Description);

public sealed record UpdateWorkflowRequestDto(string Name, string? Description);

public sealed record UpdateVersionSettingsRequestDto(
    string Name, string? Description, bool AllowsPendingCustomer, bool AllowsPendingInternal, bool RequiresApproval);

public sealed record SaveStepRequestDto(string Name, WorkflowStepKind Kind, bool IsOptional, ApprovalType? ApprovalType);

public enum StepMoveDirection
{
    Up,
    Down
}

public sealed record MoveStepRequestDto(StepMoveDirection Direction);

public sealed record SetTransitionRequestDto(WorkflowStepOutcome Outcome, int? TargetStepId);

public static class ApprovalTypeLabels
{
    public static string Label(ApprovalType type) => type switch
    {
        ApprovalType.AccountingApproval => "Accounting Approval",
        ApprovalType.CustomerServiceApproval => "Customer Service Approval",
        _ => type.ToString()
    };
}

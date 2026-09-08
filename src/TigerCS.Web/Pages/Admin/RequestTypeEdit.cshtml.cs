using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Web.Models;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Admin;

/// <summary>"Add Request Type" (no id) and "Manage Request Type" (id): the request type itself, its workflow link, assignment rule, approval requirements and SLA rows.</summary>
public sealed class RequestTypeEditModel(AdminApiClient adminApi, DepartmentsApiClient departmentsApi) : AdminPageModel
{
    public int? RequestTypeId { get; private set; }
    public bool IsNew => RequestTypeId is null;
    public AdminRequestTypeDetailDto? RequestType { get; private set; }
    public IReadOnlyCollection<DepartmentDto> Departments { get; private set; } = [];
    public IReadOnlyList<AdminWorkflowSummaryDto> Workflows { get; private set; } = [];
    public IReadOnlyList<DepartmentMemberDto> DepartmentMembers { get; private set; } = [];
    public IReadOnlyList<AdminUserDto> AllUsers { get; private set; } = [];
    public string? LoadError { get; private set; }

    public static IReadOnlyList<string> FixedRoles => Roles.All;

    public static string PriorityLabel(byte priorityId) => TicketDisplay.PriorityLabel(priorityId);

    [BindProperty] public DetailsInput Details { get; set; } = new();
    [BindProperty] public AssignmentInput Assignment { get; set; } = new();
    [BindProperty] public ApprovalInput Approval { get; set; } = new();
    [BindProperty] public SlaInput Sla { get; set; } = new();
    [BindProperty] public ActivationInput Activation { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int? id, CancellationToken cancellationToken)
    {
        RequestTypeId = id;
        await LoadReferenceDataAsync(cancellationToken);

        if (id is { } requestTypeId)
        {
            var result = await adminApi.GetRequestTypeAsync(requestTypeId, cancellationToken);
            if (result.Outcome == ApiOutcome.NotFound)
            {
                return NotFound();
            }

            if (!result.IsSuccess)
            {
                LoadError = DescribeFailure(result, "The request type could not be loaded.");
                return Page();
            }

            RequestType = result.Value;
            var r = RequestType!;
            Details = new DetailsInput
            {
                Name = r.Name,
                DepartmentId = r.DepartmentId,
                WorkflowId = r.Workflow.WorkflowId,
                DefaultPriorityId = r.DefaultPriorityId,
                AllowAgentPriorityChange = r.AllowAgentPriorityChange,
                AllowPendingCustomer = r.AllowPendingCustomer,
                AllowPendingInternal = r.AllowPendingInternal,
                AllowReopen = r.AllowReopen
            };
            Assignment = new AssignmentInput
            {
                Mode = r.AssignmentRule?.Mode ?? AssignmentMode.DepartmentQueue,
                PrimaryEmployeeId = r.AssignmentRule?.PrimaryEmployeeId,
                MemberEmployeeIds = r.AssignmentRule?.Members.Select(m => m.EmployeeId).ToList() ?? [],
                TeamName = r.AssignmentRule?.TeamName
            };

            var members = await adminApi.GetDepartmentAsync(r.DepartmentId, cancellationToken);
            DepartmentMembers = members.IsSuccess ? (members.Value?.Members ?? []).Where(m => m.IsActive).ToList() : [];
        }
        else
        {
            Details.DefaultPriorityId = (byte)PriorityLevel.Medium;
            Details.AllowReopen = true;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        RequestTypeId = null;
        var result = await adminApi.CreateRequestTypeAsync(ToSaveRequest(), cancellationToken);
        if (result.IsSuccess)
        {
            StatusMessage = $"Request type '{result.Value!.Name}' created.";
            return RedirectToPage("/Admin/RequestTypeEdit", new { id = result.Value.RequestTypeId });
        }

        ModelState.Clear();
        ErrorMessage = DescribeFailure(result, "The request type could not be created.");
        await LoadReferenceDataAsync(cancellationToken);
        return Page();
    }

    public Task<IActionResult> OnPostDetailsAsync(int id, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.UpdateRequestTypeAsync(id, ToSaveRequest(), cancellationToken), "Request type saved.", "The request type could not be saved.");

    public Task<IActionResult> OnPostActivationAsync(int id, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.SetRequestTypeActivationAsync(id, new SetActiveRequestDto(Activation.IsActive, Activation.Reason), cancellationToken),
            Activation.IsActive ? "Request type activated." : "Request type deactivated. Existing tickets are unaffected.",
            "The request type's status could not be changed.");

    public Task<IActionResult> OnPostAssignmentAsync(int id, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.SaveAssignmentRuleAsync(id,
                new SaveAssignmentRuleRequestDto(Assignment.Mode, Assignment.PrimaryEmployeeId, Assignment.MemberEmployeeIds, Assignment.TeamName, IsActive: true),
                cancellationToken),
            "Assignment configuration saved.", "The assignment configuration could not be saved.");

    public Task<IActionResult> OnPostApprovalAsync(int id, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.SaveApprovalRequirementAsync(id, Approval.ApprovalType,
                new SaveApprovalRequirementRequestDto(Approval.TargetKind, Approval.TargetDepartmentId,
                    string.IsNullOrWhiteSpace(Approval.TargetRoleName) ? null : Approval.TargetRoleName,
                    Approval.TargetEmployeeId, Approval.BlocksWorkUntilApproved, Approval.IsActive),
                cancellationToken),
            "Approval requirement saved.", "The approval requirement could not be saved.");

    public Task<IActionResult> OnPostSlaAsync(int id, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.SaveSlaPolicyAsync(id, Sla.PriorityId,
                new SaveSlaPolicyRequestDto(Sla.Trigger, Sla.Unit,
                    Sla.FirstResponseTargetValue, Sla.FirstResponseMaximumValue,
                    Sla.ResolutionTargetValue, Sla.ResolutionMaximumValue,
                    Sla.IsImmediate, Sla.ClockBasis, TriState(Sla.PausesOnPendingCustomer), TriState(Sla.PausesOnPendingInternal),
                    Sla.WarningThresholdPercent, Sla.IsActive),
                cancellationToken),
            "SLA values saved.", "The SLA values could not be saved.");

    public async Task<IActionResult> OnPostCreateVersionAsync(int id, CancellationToken cancellationToken)
    {
        var requestType = await adminApi.GetRequestTypeAsync(id, cancellationToken);
        if (!requestType.IsSuccess)
        {
            ErrorMessage = DescribeFailure(requestType, "The request type could not be loaded.");
            return RedirectToPage("/Admin/RequestTypeEdit", new { id });
        }

        var result = await adminApi.CreateWorkflowVersionAsync(requestType.Value!.Workflow.WorkflowId, cancellationToken);
        if (result.IsSuccess)
        {
            StatusMessage = $"Draft V{result.Value!.VersionNumber} created from the active version.";
            return RedirectToPage("/Admin/WorkflowVersion", new { id = result.Value.WorkflowTemplateId });
        }

        ErrorMessage = DescribeFailure(result, "A new version could not be created.");
        return RedirectToPage("/Admin/RequestTypeEdit", new { id });
    }

    private SaveRequestTypeRequestDto ToSaveRequest() => new(
        Details.DepartmentId, Details.Name, Details.WorkflowId, Details.DefaultPriorityId,
        Details.AllowAgentPriorityChange, Details.AllowPendingCustomer, Details.AllowPendingInternal, Details.AllowReopen);

    private static bool? TriState(string? value) => value switch { "true" => true, "false" => false, _ => null };

    private async Task<IActionResult> ApplyAsync(int id, Task<ApiResult<AdminRequestTypeDetailDto>> call, string success, string failure)
    {
        var result = await call;
        if (result.IsSuccess)
        {
            StatusMessage = success;
        }
        else
        {
            ErrorMessage = DescribeFailure(result, failure);
        }

        return RedirectToPage("/Admin/RequestTypeEdit", new { id });
    }

    private async Task LoadReferenceDataAsync(CancellationToken cancellationToken)
    {
        var departments = departmentsApi.GetDepartmentsAsync(activeOnly: false, cancellationToken);
        var workflows = adminApi.GetWorkflowsAsync(includeInactive: false, cancellationToken);
        var users = adminApi.GetUsersAsync(null, includeInactive: false, 1, 100, cancellationToken);
        await Task.WhenAll(departments, workflows, users);
        Departments = departments.Result.IsSuccess ? departments.Result.Value ?? [] : [];
        Workflows = workflows.Result.IsSuccess ? workflows.Result.Value ?? [] : [];
        AllUsers = users.Result.IsSuccess ? users.Result.Value?.Items ?? [] : [];
    }

    public sealed class DetailsInput
    {
        [Required] public string Name { get; set; } = string.Empty;
        public int DepartmentId { get; set; }
        public int WorkflowId { get; set; }
        public byte DefaultPriorityId { get; set; }
        public bool AllowAgentPriorityChange { get; set; }
        public bool AllowPendingCustomer { get; set; }
        public bool AllowPendingInternal { get; set; }
        public bool AllowReopen { get; set; }
    }

    public sealed class AssignmentInput
    {
        public AssignmentMode Mode { get; set; } = AssignmentMode.DepartmentQueue;
        public Guid? PrimaryEmployeeId { get; set; }
        public List<Guid> MemberEmployeeIds { get; set; } = [];
        public string? TeamName { get; set; }
    }

    public sealed class ApprovalInput
    {
        public ApprovalType ApprovalType { get; set; }
        public ApprovalTargetKind TargetKind { get; set; } = ApprovalTargetKind.Department;
        public int? TargetDepartmentId { get; set; }
        public string? TargetRoleName { get; set; }
        public Guid? TargetEmployeeId { get; set; }
        public bool BlocksWorkUntilApproved { get; set; } = true;
        public bool IsActive { get; set; } = true;
    }

    public sealed class SlaInput
    {
        public byte PriorityId { get; set; }
        public SlaTriggerType Trigger { get; set; } = SlaTriggerType.TicketCreated;
        public SlaDurationUnit Unit { get; set; } = SlaDurationUnit.Days;
        public int? FirstResponseTargetValue { get; set; }
        public int? FirstResponseMaximumValue { get; set; }
        public int? ResolutionTargetValue { get; set; }
        public int? ResolutionMaximumValue { get; set; }
        public bool IsImmediate { get; set; }
        public SlaClockBasis? ClockBasis { get; set; }
        public string? PausesOnPendingCustomer { get; set; }
        public string? PausesOnPendingInternal { get; set; }
        public decimal? WarningThresholdPercent { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public sealed class ActivationInput
    {
        public bool IsActive { get; set; }
        public string? Reason { get; set; }
    }
}

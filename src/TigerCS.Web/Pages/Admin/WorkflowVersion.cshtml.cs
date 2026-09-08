using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Admin;

/// <summary>
/// The Workflow Designer: an ordered step builder for one version. Every
/// action posts to its own handler and redirects back; on a Published or
/// Historical version the page renders read-only and the Api refuses every
/// edit anyway.
/// </summary>
public sealed class WorkflowVersionModel(AdminApiClient adminApi) : AdminPageModel
{
    public WorkflowVersionDetailDto? Version { get; private set; }
    public WorkflowDesignerCatalogDto Catalog { get; private set; } = new([], []);
    public string? LoadError { get; private set; }

    /// <summary>The step whose edit form is open (query string), so editing never needs client-side script.</summary>
    public int? EditingStepId { get; private set; }

    [BindProperty] public StepInput Step { get; set; } = new();
    [BindProperty] public SettingsInput Settings { get; set; } = new();
    [BindProperty] public TransitionInput Transition { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id, int? editStep, CancellationToken cancellationToken)
    {
        EditingStepId = editStep;
        var version = adminApi.GetWorkflowVersionAsync(id, cancellationToken);
        var catalog = adminApi.GetWorkflowCatalogAsync(cancellationToken);
        await Task.WhenAll(version, catalog);

        if (version.Result.Outcome == ApiOutcome.NotFound)
        {
            return NotFound();
        }

        if (!version.Result.IsSuccess)
        {
            LoadError = DescribeFailure(version.Result, "The workflow version could not be loaded.");
            return Page();
        }

        Version = version.Result.Value;
        Catalog = catalog.Result.IsSuccess ? catalog.Result.Value ?? Catalog : Catalog;
        Settings = new SettingsInput
        {
            Name = Version!.Name,
            Description = Version.Description,
            AllowsPendingCustomer = Version.AllowsPendingCustomer,
            AllowsPendingInternal = Version.AllowsPendingInternal,
            RequiresApproval = Version.RequiresApproval
        };

        if (editStep is { } stepId && Version.Steps.FirstOrDefault(s => s.WorkflowTemplateStepId == stepId) is { } editing)
        {
            Step = new StepInput { Name = editing.Name, Kind = editing.Kind, IsOptional = editing.IsOptional, ApprovalType = editing.ApprovalType };
        }

        return Page();
    }

    public Task<IActionResult> OnPostSettingsAsync(int id, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.UpdateVersionSettingsAsync(id,
                new UpdateVersionSettingsRequestDto(Settings.Name, Settings.Description, Settings.AllowsPendingCustomer, Settings.AllowsPendingInternal, Settings.RequiresApproval),
                cancellationToken),
            "Version settings saved.", "The version settings could not be saved.");

    public Task<IActionResult> OnPostAddStepAsync(int id, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.AddStepAsync(id, ToStepRequest(), cancellationToken), "Step added.", "The step could not be added.");

    public Task<IActionResult> OnPostUpdateStepAsync(int id, int stepId, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.UpdateStepAsync(id, stepId, ToStepRequest(), cancellationToken), "Step saved.", "The step could not be saved.");

    public Task<IActionResult> OnPostRemoveStepAsync(int id, int stepId, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.RemoveStepAsync(id, stepId, cancellationToken), "Step deleted.", "The step could not be deleted.");

    public Task<IActionResult> OnPostMoveStepAsync(int id, int stepId, string direction, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.MoveStepAsync(id, stepId,
                new MoveStepRequestDto(string.Equals(direction, "up", StringComparison.OrdinalIgnoreCase) ? StepMoveDirection.Up : StepMoveDirection.Down),
                cancellationToken),
            "Step moved.", "The step could not be moved.");

    public Task<IActionResult> OnPostTransitionAsync(int id, int stepId, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.SetTransitionAsync(id, stepId, new SetTransitionRequestDto(Transition.Outcome, Transition.TargetStepId), cancellationToken),
            Transition.TargetStepId is null ? "Branch cleared." : "Branch saved.", "The branch could not be saved.");

    public async Task<IActionResult> OnPostPublishAsync(int id, CancellationToken cancellationToken)
    {
        var result = await adminApi.PublishVersionAsync(id, cancellationToken);
        if (result.IsSuccess)
        {
            StatusMessage = $"V{result.Value!.VersionNumber} published. New tickets now use it; existing tickets keep their version.";
        }
        else
        {
            ErrorMessage = DescribeFailure(result, "The version could not be published.");
        }

        return RedirectToPage("/Admin/WorkflowVersion", new { id });
    }

    private SaveStepRequestDto ToStepRequest() => new(
        Step.Name, Step.Kind, Step.IsOptional,
        Step.Kind == WorkflowStepKind.WaitingForApproval ? Step.ApprovalType : null);

    private async Task<IActionResult> ApplyAsync(int id, Task<ApiResult<WorkflowVersionDetailDto>> call, string success, string failure)
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

        return RedirectToPage("/Admin/WorkflowVersion", new { id });
    }

    public sealed class StepInput
    {
        [Required] public string Name { get; set; } = string.Empty;
        public WorkflowStepKind Kind { get; set; } = WorkflowStepKind.InProgress;
        public bool IsOptional { get; set; }
        public ApprovalType? ApprovalType { get; set; }
    }

    public sealed class SettingsInput
    {
        [Required] public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool AllowsPendingCustomer { get; set; }
        public bool AllowsPendingInternal { get; set; }
        public bool RequiresApproval { get; set; }
    }

    public sealed class TransitionInput
    {
        public WorkflowStepOutcome Outcome { get; set; }
        public int? TargetStepId { get; set; }
    }
}

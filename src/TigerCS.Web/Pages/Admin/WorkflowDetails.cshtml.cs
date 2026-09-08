using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Admin;

public sealed class WorkflowDetailsModel(AdminApiClient adminApi) : AdminPageModel
{
    public AdminWorkflowDetailDto? Workflow { get; private set; }
    public string? LoadError { get; private set; }

    [BindProperty] public EditInput Edit { get; set; } = new();
    [BindProperty] public ActivationInput Activation { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var result = await adminApi.GetWorkflowAsync(id, cancellationToken);
        if (result.Outcome == ApiOutcome.NotFound)
        {
            return NotFound();
        }

        if (!result.IsSuccess)
        {
            LoadError = DescribeFailure(result, "The workflow could not be loaded.");
            return Page();
        }

        Workflow = result.Value;
        Edit = new EditInput { Name = Workflow!.Name, Description = Workflow.Description };
        return Page();
    }

    public async Task<IActionResult> OnPostEditAsync(int id, CancellationToken cancellationToken)
    {
        var result = await adminApi.UpdateWorkflowAsync(id, new UpdateWorkflowRequestDto(Edit.Name, Edit.Description), cancellationToken);
        SetMessages(result.IsSuccess, "Workflow saved.", DescribeFailure(result, "The workflow could not be saved."));
        return RedirectToPage("/Admin/WorkflowDetails", new { id });
    }

    public async Task<IActionResult> OnPostActivationAsync(int id, CancellationToken cancellationToken)
    {
        var result = await adminApi.SetWorkflowActivationAsync(id, new SetActiveRequestDto(Activation.IsActive, Activation.Reason), cancellationToken);
        SetMessages(result.IsSuccess,
            Activation.IsActive ? "Workflow activated." : "Workflow deactivated. Its versions and existing tickets are untouched.",
            DescribeFailure(result, "The workflow's status could not be changed."));
        return RedirectToPage("/Admin/WorkflowDetails", new { id });
    }

    public async Task<IActionResult> OnPostCreateVersionAsync(int id, CancellationToken cancellationToken)
    {
        var result = await adminApi.CreateWorkflowVersionAsync(id, cancellationToken);
        if (result.IsSuccess)
        {
            StatusMessage = $"Draft V{result.Value!.VersionNumber} created. Edit it, then publish.";
            return RedirectToPage("/Admin/WorkflowVersion", new { id = result.Value.WorkflowTemplateId });
        }

        ErrorMessage = DescribeFailure(result, "A new version could not be created.");
        return RedirectToPage("/Admin/WorkflowDetails", new { id });
    }

    public async Task<IActionResult> OnPostDeleteDraftAsync(int id, int versionId, CancellationToken cancellationToken)
    {
        var result = await adminApi.DeleteDraftAsync(versionId, cancellationToken);
        SetMessages(result.IsSuccess, "Draft deleted.", DescribeFailure(result, "The draft could not be deleted."));
        return RedirectToPage("/Admin/WorkflowDetails", new { id });
    }

    private void SetMessages(bool success, string successMessage, string failureMessage)
    {
        if (success)
        {
            StatusMessage = successMessage;
        }
        else
        {
            ErrorMessage = failureMessage;
        }
    }

    public sealed class EditInput
    {
        [Required] public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    public sealed class ActivationInput
    {
        public bool IsActive { get; set; }
        public string? Reason { get; set; }
    }
}

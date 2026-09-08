using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Admin;

public sealed class WorkflowsModel(AdminApiClient adminApi) : AdminPageModel
{
    public IReadOnlyList<AdminWorkflowSummaryDto> Workflows { get; private set; } = [];
    public bool IncludeInactive { get; private set; } = true;
    public string? LoadError { get; private set; }

    [BindProperty] public CreateInput Create { get; set; } = new();

    public async Task OnGetAsync(bool? includeInactive, CancellationToken cancellationToken)
    {
        IncludeInactive = includeInactive ?? true;
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        var result = await adminApi.CreateWorkflowAsync(new CreateWorkflowRequestDto(Create.Name, Create.Description), cancellationToken);
        if (result.IsSuccess)
        {
            var draft = result.Value!.Versions.FirstOrDefault(v => v.Status == Domain.Modules.WorkflowConfiguration.WorkflowVersionStatus.Draft);
            StatusMessage = $"Workflow '{result.Value.Name}' created with Draft V1. Design its steps, then publish.";
            return draft is null
                ? RedirectToPage("/Admin/WorkflowDetails", new { id = result.Value.WorkflowId })
                : RedirectToPage("/Admin/WorkflowVersion", new { id = draft.WorkflowTemplateId });
        }

        ErrorMessage = DescribeFailure(result, "The workflow could not be created.");
        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var result = await adminApi.GetWorkflowsAsync(IncludeInactive, cancellationToken);
        if (result.IsSuccess)
        {
            Workflows = result.Value ?? [];
        }
        else
        {
            LoadError = DescribeFailure(result, "The workflow list could not be loaded.");
        }
    }

    public sealed class CreateInput
    {
        [Required] public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}

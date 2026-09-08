using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Admin;

public sealed class DepartmentsModel(AdminApiClient adminApi) : AdminPageModel
{
    public IReadOnlyList<AdminDepartmentDto> Departments { get; private set; } = [];
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
        var result = await adminApi.CreateDepartmentAsync(new SaveDepartmentRequestDto(Create.Name, Create.Code), cancellationToken);
        if (result.IsSuccess)
        {
            StatusMessage = $"Department '{result.Value!.Name}' created.";
            return RedirectToPage("/Admin/DepartmentEdit", new { id = result.Value.DepartmentId });
        }

        ErrorMessage = DescribeFailure(result, "The department could not be created.");
        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var result = await adminApi.GetDepartmentsAsync(IncludeInactive, cancellationToken);
        if (result.IsSuccess)
        {
            Departments = result.Value ?? [];
        }
        else
        {
            LoadError = DescribeFailure(result, "The department list could not be loaded.");
        }
    }

    public sealed class CreateInput
    {
        [Required] public string Name { get; set; } = string.Empty;
        [Required] public string Code { get; set; } = string.Empty;
    }
}

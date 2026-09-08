using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Admin;

public sealed class DepartmentEditModel(AdminApiClient adminApi) : AdminPageModel
{
    public int DepartmentId { get; private set; }
    public AdminDepartmentDetailDto? Department { get; private set; }
    public IReadOnlyList<AdminUserDto> CandidateUsers { get; private set; } = [];
    public string? LoadError { get; private set; }

    [BindProperty] public EditInput Edit { get; set; } = new();
    [BindProperty] public MemberInput Member { get; set; } = new();
    [BindProperty] public ActivationInput Activation { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        DepartmentId = id;
        var result = await adminApi.GetDepartmentAsync(id, cancellationToken);
        if (result.Outcome == ApiOutcome.NotFound)
        {
            return NotFound();
        }

        if (!result.IsSuccess)
        {
            LoadError = DescribeFailure(result, "The department could not be loaded.");
            return Page();
        }

        Department = result.Value;
        Edit = new EditInput { Name = Department!.Name, Code = Department.Code };

        var users = await adminApi.GetUsersAsync(null, includeInactive: false, 1, 100, cancellationToken);
        var memberIds = Department.Members.Select(m => m.EmployeeId).ToHashSet();
        CandidateUsers = users.IsSuccess
            ? (users.Value?.Items ?? []).Where(u => !memberIds.Contains(u.EmployeeId)).ToList()
            : [];

        return Page();
    }

    public Task<IActionResult> OnPostEditAsync(int id, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.UpdateDepartmentAsync(id, new SaveDepartmentRequestDto(Edit.Name, Edit.Code), cancellationToken),
            "Department saved.", "The department could not be saved.");

    public Task<IActionResult> OnPostActivationAsync(int id, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.SetDepartmentActivationAsync(id, new SetActiveRequestDto(Activation.IsActive, Activation.Reason), cancellationToken),
            Activation.IsActive ? "Department activated." : "Department deactivated. Historical tickets keep its name.",
            "The department's status could not be changed.");

    public Task<IActionResult> OnPostAddMemberAsync(int id, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.AddDepartmentMemberAsync(id, new AddDepartmentMemberRequestDto(Member.EmployeeId, Member.IsPrimary), cancellationToken),
            "Member added.", "The member could not be added.");

    public Task<IActionResult> OnPostRemoveMemberAsync(int id, Guid employeeId, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.RemoveDepartmentMemberAsync(id, employeeId, cancellationToken),
            "Member removed.", "The member could not be removed.");

    private async Task<IActionResult> ApplyAsync(int id, Task<ApiResult<AdminDepartmentDetailDto>> call, string success, string failure)
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

        return RedirectToPage("/Admin/DepartmentEdit", new { id });
    }

    public sealed class EditInput
    {
        [Required] public string Name { get; set; } = string.Empty;
        [Required] public string Code { get; set; } = string.Empty;
    }

    public sealed class MemberInput
    {
        public Guid EmployeeId { get; set; }
        public bool IsPrimary { get; set; }
    }

    public sealed class ActivationInput
    {
        public bool IsActive { get; set; }
        public string? Reason { get; set; }
    }
}

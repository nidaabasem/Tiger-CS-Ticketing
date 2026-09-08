using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Admin;

/// <summary>
/// One page for "Add User" (no id) and "Manage User" (id). Every section
/// posts to its own handler and redirects back, so a failure on one form
/// never loses the others' state.
/// </summary>
public sealed class UserEditModel(AdminApiClient adminApi, DepartmentsApiClient departmentsApi) : AdminPageModel
{
    public Guid? EmployeeId { get; private set; }
    public bool IsNew => EmployeeId is null;
    public AdminUserDto? UserRecord { get; private set; }
    public IReadOnlyCollection<RoleDto> Roles { get; private set; } = [];
    public IReadOnlyCollection<DepartmentDto> ActiveDepartments { get; private set; } = [];
    public string? LoadError { get; private set; }

    [BindProperty] public CreateInput Create { get; set; } = new();
    [BindProperty] public ProfileInput Profile { get; set; } = new();
    [BindProperty] public RolesInput RolesForm { get; set; } = new();
    [BindProperty] public MembershipInput Membership { get; set; } = new();
    [BindProperty] public ActivationInput Activation { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid? id, CancellationToken cancellationToken)
    {
        EmployeeId = id;
        await LoadReferenceDataAsync(cancellationToken);

        if (id is { } employeeId)
        {
            var result = await adminApi.GetUserAsync(employeeId, cancellationToken);
            if (result.Outcome == ApiOutcome.NotFound)
            {
                return NotFound();
            }

            if (!result.IsSuccess)
            {
                LoadError = DescribeFailure(result, "The user could not be loaded.");
                return Page();
            }

            UserRecord = result.Value;
            Profile = new ProfileInput
            {
                DisplayName = UserRecord!.DisplayName,
                Email = UserRecord.Email,
                IsGeynessStaff = UserRecord.IsGeynessStaff
            };
            RolesForm = new RolesInput { Roles = UserRecord.Roles.ToList() };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        EmployeeId = null;
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Create.UserName)) errors.Add("User name is required.");
        if (string.IsNullOrWhiteSpace(Create.DisplayName)) errors.Add("Display name is required.");
        if (string.IsNullOrWhiteSpace(Create.InitialPassword)) errors.Add("An initial password is required.");
        if (Create.InitialPassword != Create.ConfirmPassword) errors.Add("The two passwords do not match.");
        if (errors.Count > 0)
        {
            ModelState.Clear();
            ErrorMessage = string.Join(" ", errors);
            await LoadReferenceDataAsync(cancellationToken);
            return Page();
        }

        var result = await adminApi.CreateUserAsync(
            new CreateUserRequestDto(
                Create.UserName.Trim(), Create.Email, Create.DisplayName.Trim(), Create.IsGeynessStaff,
                Create.InitialPassword, Create.Roles ?? [], Create.PrimaryDepartmentId),
            cancellationToken);

        if (!result.IsSuccess)
        {
            ModelState.Clear();
            ErrorMessage = DescribeFailure(result, "The user could not be created.");
            await LoadReferenceDataAsync(cancellationToken);
            return Page();
        }

        StatusMessage = $"User '{result.Value!.DisplayName}' created.";
        return RedirectToPage("/Admin/UserEdit", new { id = result.Value.EmployeeId });
    }

    public Task<IActionResult> OnPostProfileAsync(Guid id, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.UpdateUserProfileAsync(id, new UpdateUserProfileRequestDto(Profile.DisplayName, Profile.Email, Profile.IsGeynessStaff), cancellationToken),
            "Profile saved.", "The profile could not be saved.");

    public Task<IActionResult> OnPostRolesAsync(Guid id, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.SetUserRolesAsync(id, new SetUserRolesRequestDto(RolesForm.Roles ?? []), cancellationToken),
            "Roles updated.", "The roles could not be updated.");

    public Task<IActionResult> OnPostActivationAsync(Guid id, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.SetUserActivationAsync(id, new SetActiveRequestDto(Activation.IsActive, Activation.Reason), cancellationToken),
            Activation.IsActive ? "User activated." : "User deactivated. Their history is unchanged.",
            Activation.IsActive ? "The user could not be activated." : "The user could not be deactivated.");

    public Task<IActionResult> OnPostAddDepartmentAsync(Guid id, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.AddUserDepartmentAsync(id, new AddDepartmentMembershipRequestDto(Membership.DepartmentId, Membership.IsPrimary), cancellationToken),
            "Department membership added.", "The department membership could not be added.");

    public Task<IActionResult> OnPostRemoveDepartmentAsync(Guid id, int departmentId, CancellationToken cancellationToken) =>
        ApplyAsync(id, adminApi.RemoveUserDepartmentAsync(id, departmentId, cancellationToken),
            "Department membership removed.", "The department membership could not be removed.");

    private async Task<IActionResult> ApplyAsync(Guid id, Task<ApiResult<AdminUserDto>> call, string success, string failure)
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

        return RedirectToPage("/Admin/UserEdit", new { id });
    }

    private async Task LoadReferenceDataAsync(CancellationToken cancellationToken)
    {
        var roles = adminApi.GetRolesAsync(cancellationToken);
        var departments = departmentsApi.GetDepartmentsAsync(activeOnly: true, cancellationToken);
        await Task.WhenAll(roles, departments);
        Roles = roles.Result.IsSuccess ? roles.Result.Value ?? [] : [];
        ActiveDepartments = departments.Result.IsSuccess ? departments.Result.Value ?? [] : [];
    }

    public sealed class CreateInput
    {
        [Required] public string UserName { get; set; } = string.Empty;
        public string? Email { get; set; }
        [Required] public string DisplayName { get; set; } = string.Empty;
        public bool IsGeynessStaff { get; set; }
        [Required] public string InitialPassword { get; set; } = string.Empty;
        public string ConfirmPassword { get; set; } = string.Empty;
        public List<string>? Roles { get; set; }
        public int? PrimaryDepartmentId { get; set; }
    }

    public sealed class ProfileInput
    {
        [Required] public string DisplayName { get; set; } = string.Empty;
        public string? Email { get; set; }
        public bool IsGeynessStaff { get; set; }
    }

    public sealed class RolesInput
    {
        public List<string>? Roles { get; set; }
    }

    public sealed class MembershipInput
    {
        public int DepartmentId { get; set; }
        public bool IsPrimary { get; set; }
    }

    public sealed class ActivationInput
    {
        public bool IsActive { get; set; }
        public string? Reason { get; set; }
    }
}

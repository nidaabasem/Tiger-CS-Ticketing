using TigerCS.Application.Modules.IdentityAndAccess.Dto;

namespace TigerCS.Application.Modules.Administration.Dto;

public sealed record AdminUserDto(
    Guid EmployeeId,
    string UserName,
    string? Email,
    string DisplayName,
    bool IsGeynessStaff,
    bool IsActive,
    DateTime? DeactivatedAtUtc,
    DateTime CreatedAtUtc,
    bool IsLockedOut,
    IReadOnlyList<string> Roles,
    IReadOnlyList<DepartmentMembershipDto> Departments,
    bool HasHistory);

public sealed record AdminUserListDto(IReadOnlyList<AdminUserDto> Items, int Page, int PageSize, int TotalCount);

/// <summary>
/// Creates the Identity account AND the employee profile in one step. The
/// initial password is validated by ASP.NET Core Identity's own password
/// policy (the same path the development seed uses) — no custom password
/// handling exists, and there is no reset flow in this phase.
/// </summary>
public sealed record CreateUserRequestDto(
    string UserName,
    string? Email,
    string DisplayName,
    bool IsGeynessStaff,
    string InitialPassword,
    IReadOnlyList<string> Roles,
    int? PrimaryDepartmentId);

public sealed record UpdateUserProfileRequestDto(string DisplayName, string? Email, bool IsGeynessStaff);

public sealed record SetUserRolesRequestDto(IReadOnlyList<string> Roles);

public sealed record AddDepartmentMembershipRequestDto(int DepartmentId, bool IsPrimary);

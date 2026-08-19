namespace TigerCS.Application.Modules.IdentityAndAccess.Dto;

// MVP-API-Contracts.md §1.3 GET /api/users/me
public sealed record DepartmentMembershipDto(int DepartmentId, string Name, bool IsPrimary);

public sealed record CurrentUserResponseDto(
    Guid EmployeeId,
    string DisplayName,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<DepartmentMembershipDto> Departments,
    bool IsGeynessStaff);

// MVP-API-Contracts.md §1.4 GET /api/departments/{departmentId}/users
public sealed record DepartmentUserDto(
    Guid EmployeeId,
    string DisplayName,
    bool IsPrimary,
    IReadOnlyCollection<string> Roles);

public sealed record PagedResultDto<T>(IReadOnlyCollection<T> Items, int Page, int PageSize, int TotalCount);

// MVP-API-Contracts.md §1.5 GET /api/roles
public sealed record RoleDto(string RoleName, string Description);

// MVP-API-Contracts.md §1.6 PATCH /api/users/{employeeId}/activation
public sealed record ActivationRequestDto(bool IsActive, string? Reason);

public sealed record ActivationResponseDto(
    Guid EmployeeId,
    string DisplayName,
    bool IsActive,
    DateTime? DeactivatedAtUtc,
    int AffectedOpenTicketCount);

public enum ActivationOutcome
{
    Success,
    NotFound,
    LastActiveAdministrator
}

public sealed record ActivationResult(ActivationOutcome Outcome, ActivationResponseDto? Response = null)
{
    public static ActivationResult Success(ActivationResponseDto response) => new(ActivationOutcome.Success, response);
    public static ActivationResult NotFound() => new(ActivationOutcome.NotFound);
    public static ActivationResult LastActiveAdministrator() => new(ActivationOutcome.LastActiveAdministrator);
}

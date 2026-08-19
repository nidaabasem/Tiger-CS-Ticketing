using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;

namespace TigerCS.Application.Modules.IdentityAndAccess.Services;

/// <summary>MVP-API-Contracts.md §1.3 GET /api/users/me.</summary>
public sealed class UserProfileAppService(
    IEmployeeRepository employeeRepository,
    IUserRoleReader roleReader,
    IUserDepartmentAssignmentRepository assignmentRepository)
{
    public async Task<CurrentUserResponseDto?> GetCurrentUserAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        var employee = await employeeRepository.GetByIdAsync(employeeId, cancellationToken);
        if (employee is null)
        {
            return null;
        }

        var roles = await roleReader.GetRolesAsync(employeeId, cancellationToken);
        var assignments = await assignmentRepository.GetByEmployeeIdAsync(employeeId, cancellationToken);

        var departments = assignments
            .Select(a => new DepartmentMembershipDto(a.DepartmentId, a.Department.Name, a.IsPrimary))
            .ToList();

        return new CurrentUserResponseDto(employee.EmployeeId, employee.DisplayName, roles, departments, employee.IsGeynessStaff);
    }
}

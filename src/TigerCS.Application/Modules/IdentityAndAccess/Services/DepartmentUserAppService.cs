using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;

namespace TigerCS.Application.Modules.IdentityAndAccess.Services;

/// <summary>MVP-API-Contracts.md §1.4 GET /api/departments/{departmentId}/users.</summary>
public sealed class DepartmentUserAppService(
    IDepartmentRepository departmentRepository,
    IUserDepartmentAssignmentRepository assignmentRepository,
    IUserRoleReader roleReader)
{
    /// <summary>Returns null if the department does not exist (maps to 404 at the API layer).</summary>
    public async Task<PagedResultDto<DepartmentUserDto>?> ListAsync(
        int departmentId, bool activeOnly, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var department = await departmentRepository.GetByIdAsync(departmentId, cancellationToken);
        if (department is null)
        {
            return null;
        }

        var assignments = await assignmentRepository.GetByDepartmentIdAsync(departmentId, activeOnly, cancellationToken);
        var ordered = assignments.OrderBy(a => a.Employee.DisplayName).ToList();

        var items = new List<DepartmentUserDto>();
        foreach (var assignment in ordered.Skip((page - 1) * pageSize).Take(pageSize))
        {
            var roles = await roleReader.GetRolesAsync(assignment.EmployeeId, cancellationToken);
            items.Add(new DepartmentUserDto(assignment.EmployeeId, assignment.Employee.DisplayName, assignment.IsPrimary, roles));
        }

        return new PagedResultDto<DepartmentUserDto>(items, page, pageSize, ordered.Count);
    }
}

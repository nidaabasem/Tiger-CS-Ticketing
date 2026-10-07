using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;

namespace TigerCS.Application.Modules.IdentityAndAccess.Services;

/// <summary>
/// The cross-department assignee directory: every active employee who is a
/// member of at least one active department, with their roles and ALL their
/// department memberships (primary first). Backs
/// <c>GET /api/users/assignable</c>, which a CS Manager reads when the
/// ticket's current department holds nobody suitable and the ticket must be
/// transferred to the assignee's department and assigned in one step.
/// </summary>
/// <remarks>
/// <para>
/// One row per employee, however many departments they belong to — the
/// UI chooses the (employee, department) pair, and the assignment rule
/// ("the assignee belongs to the ticket's resulting department") is enforced
/// by <c>TicketAssignmentAppService</c>, never by this listing.
/// </para>
/// <para>
/// This is a directory, not an authorization decision: it lists who exists.
/// Whether the caller may assign at all is the endpoint's policy and the
/// assignment service's role gate.
/// </para>
/// </remarks>
public sealed class AssignableUserAppService(
    IUserDepartmentAssignmentRepository assignmentRepository,
    IUserRoleReader roleReader)
{
    public async Task<IReadOnlyList<AssignableUserDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var memberships = await assignmentRepository.ListActiveMembershipsAsync(cancellationToken);

        var result = new List<AssignableUserDto>();
        foreach (var group in memberships
                     .GroupBy(m => m.EmployeeId)
                     .OrderBy(g => g.First().Employee.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var roles = await roleReader.GetRolesAsync(group.Key, cancellationToken);
            var departments = group
                .OrderByDescending(m => m.IsPrimary)
                .ThenBy(m => m.Department.Name, StringComparer.OrdinalIgnoreCase)
                .Select(m => new DepartmentMembershipDto(m.DepartmentId, m.Department.Name, m.IsPrimary))
                .ToList();

            result.Add(new AssignableUserDto(group.Key, group.First().Employee.DisplayName, roles, departments));
        }

        return result;
    }
}

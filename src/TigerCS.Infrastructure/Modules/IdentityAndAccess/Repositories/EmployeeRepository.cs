using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;

public sealed class EmployeeRepository(TigerCsDbContext dbContext) : IEmployeeRepository
{
    public Task<Employee?> GetByIdAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        dbContext.Employees.FirstOrDefaultAsync(e => e.EmployeeId == employeeId, cancellationToken);

    public async Task AddAsync(Employee employee, CancellationToken cancellationToken = default) =>
        await dbContext.Employees.AddAsync(employee, cancellationToken);

    public async Task<int> CountActiveInRoleAsync(string roleName, CancellationToken cancellationToken = default)
    {
        var role = await dbContext.Roles.FirstOrDefaultAsync(r => r.Name == roleName, cancellationToken);
        if (role is null)
        {
            return 0;
        }

        var employeeIdsInRole = dbContext.UserRoles
            .Where(ur => ur.RoleId == role.Id)
            .Select(ur => ur.UserId);

        return await dbContext.Employees
            .Where(e => e.DeactivatedAtUtc == null && employeeIdsInRole.Contains(e.EmployeeId))
            .CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Employee>> ListAsync(
        bool includeInactive, IReadOnlyCollection<Guid>? employeeIds, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Employees.Include(e => e.DepartmentAssignments).ThenInclude(a => a.Department).AsQueryable();
        if (!includeInactive)
        {
            query = query.Where(e => e.DeactivatedAtUtc == null);
        }

        if (employeeIds is not null)
        {
            query = query.Where(e => employeeIds.Contains(e.EmployeeId));
        }

        return await query.OrderBy(e => e.DisplayName).ToListAsync(cancellationToken);
    }

    public async Task<bool> IsReferencedByHistoryAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        await dbContext.Tickets.AnyAsync(t => t.CurrentOwnerEmployeeId == employeeId, cancellationToken)
        || await dbContext.TicketAssignments.AnyAsync(a => a.AssignedEmployeeId == employeeId || a.AssigningActorEmployeeId == employeeId, cancellationToken)
        || await dbContext.TicketApprovals.AnyAsync(a => a.RequestedByEmployeeId == employeeId || a.DecidedByEmployeeId == employeeId, cancellationToken)
        || await dbContext.TicketStatusHistoryEntries.AnyAsync(h => h.ActorEmployeeId == employeeId, cancellationToken)
        || await dbContext.AuditEntries.AnyAsync(a => a.ActorEmployeeId == employeeId, cancellationToken);
}

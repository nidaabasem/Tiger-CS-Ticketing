using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;

public sealed class UserDepartmentAssignmentRepository(TigerCsDbContext dbContext) : IUserDepartmentAssignmentRepository
{
    public async Task<IReadOnlyCollection<UserDepartmentAssignment>> GetByEmployeeIdAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        await dbContext.UserDepartmentAssignments
            .Include(a => a.Department)
            .Where(a => a.EmployeeId == employeeId)
            .ToListAsync(cancellationToken);

    public Task<UserDepartmentAssignment?> GetPrimaryAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        dbContext.UserDepartmentAssignments
            .FirstOrDefaultAsync(a => a.EmployeeId == employeeId && a.IsPrimary, cancellationToken);

    public Task<bool> ExistsAsync(Guid employeeId, int departmentId, CancellationToken cancellationToken = default) =>
        dbContext.UserDepartmentAssignments
            .AnyAsync(a => a.EmployeeId == employeeId && a.DepartmentId == departmentId, cancellationToken);

    public async Task<IReadOnlyCollection<UserDepartmentAssignment>> GetByDepartmentIdAsync(
        int departmentId, bool activeEmployeesOnly, CancellationToken cancellationToken = default)
    {
        var query = dbContext.UserDepartmentAssignments
            .Include(a => a.Employee)
            .Include(a => a.Department)
            .Where(a => a.DepartmentId == departmentId);

        if (activeEmployeesOnly)
        {
            query = query.Where(a => a.Employee.DeactivatedAtUtc == null);
        }

        return await query.ToListAsync(cancellationToken);
    }

    public async Task AddAsync(UserDepartmentAssignment assignment, CancellationToken cancellationToken = default) =>
        await dbContext.UserDepartmentAssignments.AddAsync(assignment, cancellationToken);
}

public sealed class IdentityUnitOfWork(TigerCsDbContext dbContext) : IIdentityUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);
}

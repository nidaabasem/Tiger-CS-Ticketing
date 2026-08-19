using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;

/// <summary>Module-Design.md's "Identity and Access" public interface implementation.</summary>
public sealed class EmployeeDirectory(TigerCsDbContext dbContext) : IEmployeeDirectory
{
    public Task<Employee?> FindByIdAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        dbContext.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.EmployeeId == employeeId, cancellationToken);

    public async Task<bool> IsActiveAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        var employee = await dbContext.Employees.AsNoTracking()
            .FirstOrDefaultAsync(e => e.EmployeeId == employeeId, cancellationToken);
        return employee is { IsActive: true };
    }

    public async Task<IReadOnlyCollection<UserDepartmentAssignment>> GetDepartmentAssignmentsAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        await dbContext.UserDepartmentAssignments.AsNoTracking()
            .Where(a => a.EmployeeId == employeeId)
            .ToListAsync(cancellationToken);
}

using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;

public sealed class DepartmentRepository(TigerCsDbContext dbContext) : IDepartmentRepository
{
    public Task<Department?> GetByIdAsync(int departmentId, CancellationToken cancellationToken = default) =>
        dbContext.Departments.FirstOrDefaultAsync(d => d.DepartmentId == departmentId, cancellationToken);

    public async Task<IReadOnlyCollection<Department>> ListAsync(bool activeOnly, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Departments.AsQueryable();
        if (activeOnly)
        {
            query = query.Where(d => d.IsActive);
        }

        return await query.OrderBy(d => d.Name).ToListAsync(cancellationToken);
    }
}

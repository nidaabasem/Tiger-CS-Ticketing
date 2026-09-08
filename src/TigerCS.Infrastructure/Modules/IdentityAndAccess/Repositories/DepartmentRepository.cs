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

    public async Task AddAsync(Department department, CancellationToken cancellationToken = default) =>
        await dbContext.Departments.AddAsync(department, cancellationToken);

    public Task<bool> NameExistsAsync(string name, int? excludeDepartmentId, CancellationToken cancellationToken = default) =>
        dbContext.Departments.AnyAsync(
            d => d.Name == name && (excludeDepartmentId == null || d.DepartmentId != excludeDepartmentId), cancellationToken);

    public Task<bool> CodeExistsAsync(string code, int? excludeDepartmentId, CancellationToken cancellationToken = default) =>
        dbContext.Departments.AnyAsync(
            d => d.Code == code && (excludeDepartmentId == null || d.DepartmentId != excludeDepartmentId), cancellationToken);

    public Task<int> CountTicketReferencesAsync(int departmentId, CancellationToken cancellationToken = default) =>
        dbContext.Tickets.CountAsync(
            t => t.CurrentDepartmentId == departmentId || t.OriginatingDepartmentId == departmentId, cancellationToken);
}

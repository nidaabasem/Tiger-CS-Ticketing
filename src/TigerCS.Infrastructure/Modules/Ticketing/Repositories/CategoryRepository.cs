using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class CategoryRepository(TigerCsDbContext dbContext) : ICategoryRepository
{
    public Task<Category?> GetByIdAsync(int categoryId, CancellationToken cancellationToken = default) =>
        dbContext.Categories.FirstOrDefaultAsync(c => c.CategoryId == categoryId, cancellationToken);

    public async Task<IReadOnlyCollection<Category>> ListAsync(
        bool activeOnly, int? departmentId, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Categories.AsQueryable();
        if (activeOnly)
        {
            query = query.Where(c => c.IsActive);
        }

        if (departmentId is { } id)
        {
            query = query.Where(c => c.DepartmentId == id);
        }

        return await query.OrderBy(c => c.Name).ToListAsync(cancellationToken);
    }
}

using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class CategoryRepository(TigerCsDbContext dbContext) : ICategoryRepository
{
    public Task<Category?> GetByIdAsync(int categoryId, CancellationToken cancellationToken = default) =>
        dbContext.Categories.FirstOrDefaultAsync(c => c.CategoryId == categoryId, cancellationToken);
}

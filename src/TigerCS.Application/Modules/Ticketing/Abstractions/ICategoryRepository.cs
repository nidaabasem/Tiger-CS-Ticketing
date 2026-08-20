using TigerCS.Domain.Modules.ClassificationAndRouting;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface ICategoryRepository
{
    Task<Category?> GetByIdAsync(int categoryId, CancellationToken cancellationToken = default);
}

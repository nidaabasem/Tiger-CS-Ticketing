using TigerCS.Domain.Modules.ClassificationAndRouting;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface ICategoryRepository
{
    Task<Category?> GetByIdAsync(int categoryId, CancellationToken cancellationToken = default);

    /// <summary>Active Categories, optionally narrowed to one Department. A Department with none configured returns an empty collection.</summary>
    Task<IReadOnlyCollection<Category>> ListAsync(bool activeOnly, int? departmentId, CancellationToken cancellationToken = default);
}

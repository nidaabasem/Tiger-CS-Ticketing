using TigerCS.Domain.Modules.ClassificationAndRouting;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface ICategoryRepository
{
    Task<Category?> GetByIdAsync(int categoryId, CancellationToken cancellationToken = default);

    /// <summary>All active categories, for the ticket-creation category picker (FR-CLS-01).</summary>
    Task<IReadOnlyList<Category>> ListActiveAsync(CancellationToken cancellationToken = default);
}

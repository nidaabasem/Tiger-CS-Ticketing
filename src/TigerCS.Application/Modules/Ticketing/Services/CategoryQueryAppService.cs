using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>The category directory (FR-CLS-01/FR-RTE-01) — read-only, used by the ticket-creation category picker.</summary>
public sealed class CategoryQueryAppService(ICategoryRepository categoryRepository)
{
    public async Task<IReadOnlyList<CategoryResponseDto>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        var categories = await categoryRepository.ListActiveAsync(cancellationToken);
        return categories.Select(c => new CategoryResponseDto(c.CategoryId, c.Name, c.DepartmentId)).ToList();
    }
}

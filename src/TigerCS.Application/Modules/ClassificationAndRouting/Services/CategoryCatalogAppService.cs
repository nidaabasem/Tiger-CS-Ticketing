using TigerCS.Application.Modules.ClassificationAndRouting.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;

namespace TigerCS.Application.Modules.ClassificationAndRouting.Services;

/// <summary>
/// <c>GET /api/categories</c>: the active Categories an agent may route a
/// ticket to, so the New Ticket UI never asks anyone to type a raw
/// <c>CategoryId</c>. Department-scoped when a Department is given (the same
/// mapping <c>TicketCreationAppService.ResolveRoutingAsync</c> already
/// enforces at creation), otherwise every active Category across every
/// Department.
/// </summary>
public sealed class CategoryCatalogAppService(ICategoryRepository categoryRepository, IDepartmentRepository departmentRepository)
{
    public async Task<IReadOnlyCollection<CategoryDto>> ListAsync(int? departmentId, CancellationToken cancellationToken = default)
    {
        var categories = await categoryRepository.ListAsync(activeOnly: true, departmentId, cancellationToken);
        if (categories.Count == 0)
        {
            return [];
        }

        // Resolved once for the whole list rather than per-category: cheap
        // even for every Department, and every Category is about to need one.
        var departments = await departmentRepository.ListAsync(activeOnly: false, cancellationToken);
        var departmentNames = departments.ToDictionary(d => d.DepartmentId, d => d.Name);

        return categories
            .Select(c => new CategoryDto(
                c.CategoryId, c.Name, c.DepartmentId, departmentNames.GetValueOrDefault(c.DepartmentId, $"Department #{c.DepartmentId}")))
            .OrderBy(c => c.DepartmentName, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
    }
}

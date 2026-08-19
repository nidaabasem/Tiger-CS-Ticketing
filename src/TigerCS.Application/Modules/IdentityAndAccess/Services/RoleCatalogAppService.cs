using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;

namespace TigerCS.Application.Modules.IdentityAndAccess.Services;

/// <summary>MVP-API-Contracts.md §1.5 GET /api/roles.</summary>
public sealed class RoleCatalogAppService(IRoleCatalogReader roleCatalogReader)
{
    public Task<IReadOnlyCollection<RoleDto>> ListRolesAsync(CancellationToken cancellationToken = default) =>
        roleCatalogReader.ListRolesAsync(cancellationToken);
}

using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;

public sealed class RoleCatalogReader(TigerCsDbContext dbContext) : IRoleCatalogReader
{
    public async Task<IReadOnlyCollection<RoleDto>> ListRolesAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Roles
            .OrderBy(r => r.Name)
            .Select(r => new RoleDto(r.Name!, r.Description))
            .ToListAsync(cancellationToken);
}

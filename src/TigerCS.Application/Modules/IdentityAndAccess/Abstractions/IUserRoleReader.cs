namespace TigerCS.Application.Modules.IdentityAndAccess.Abstractions;

/// <summary>Reads AspNetUserRoles membership without exposing Identity's UserManager to Application.</summary>
public interface IUserRoleReader
{
    Task<IReadOnlyCollection<string>> GetRolesAsync(Guid employeeId, CancellationToken cancellationToken = default);
}

/// <summary>Lists the fixed MVP role catalog (MVP-API-Contracts.md §1.5) — static/seeded, no CRUD at MVP.</summary>
public interface IRoleCatalogReader
{
    Task<IReadOnlyCollection<Dto.RoleDto>> ListRolesAsync(CancellationToken cancellationToken = default);
}

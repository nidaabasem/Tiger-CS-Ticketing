using Microsoft.AspNetCore.Identity;

namespace TigerCS.Infrastructure.Identity;

/// <summary>
/// ASP.NET Core Identity's TRole, extended with Description so
/// GET /api/roles (MVP-API-Contracts.md §1.5) can serve { RoleName,
/// Description } straight from AspNetRoles without a separate table.
/// </summary>
public class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole() { }

    public ApplicationRole(string roleName, string description) : base(roleName)
    {
        Description = description;
    }

    public string Description { get; set; } = string.Empty;
}

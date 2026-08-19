using Microsoft.AspNetCore.Identity;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Infrastructure.Identity;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;

public sealed class UserRoleReader(UserManager<ApplicationUser> userManager) : IUserRoleReader
{
    public async Task<IReadOnlyCollection<string>> GetRolesAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(employeeId.ToString());
        if (user is null)
        {
            return [];
        }

        var roles = await userManager.GetRolesAsync(user);
        return roles.ToList();
    }
}

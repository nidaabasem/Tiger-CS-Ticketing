using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

/// <summary>
/// Security-Architecture.md §14: "A deactivated Employee cannot obtain a new
/// session even if their prior token has not yet expired — deactivation is
/// checked on every request, not only at login." Added to every policy
/// (see Program.cs) so a still-valid token from a now-deactivated employee
/// is rejected.
/// </summary>
public sealed class ActiveEmployeeRequirement : IAuthorizationRequirement;

public sealed class ActiveEmployeeHandler(IEmployeeDirectory employeeDirectory)
    : AuthorizationHandler<ActiveEmployeeRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ActiveEmployeeRequirement requirement)
    {
        var idValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (idValue is null || !Guid.TryParse(idValue, out var employeeId))
        {
            return;
        }

        if (await employeeDirectory.IsActiveAsync(employeeId))
        {
            context.Succeed(requirement);
        }
    }
}

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
///
/// <para>
/// An <see cref="IIdentityGateRequirement"/>, and the only one today: it
/// establishes that the caller still has a live session at all, not what
/// they are permitted to do with it. That is why the System Administrator
/// authorization override (<see cref="SystemAdministratorOverrideHandler"/>,
/// ADR-0024) deliberately does not satisfy it — a deactivated administrator
/// holding an unexpired token is refused exactly as any other deactivated
/// employee is.
/// </para>
/// </summary>
public sealed class ActiveEmployeeRequirement : IIdentityGateRequirement;

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

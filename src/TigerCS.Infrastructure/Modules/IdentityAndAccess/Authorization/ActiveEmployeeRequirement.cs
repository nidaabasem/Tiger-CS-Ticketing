using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
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

/// <summary>
/// Two per-request checks against the database, both of which must pass:
/// the employee is still active, and the token's security-stamp claim
/// (<see cref="TigerCsTokenClaims.SecurityStamp"/>, <c>sst</c>) still
/// equals the account's current Identity security stamp.
///
/// <para>
/// The stamp check is how a stateless JWT with no revocation list is
/// invalidated by a credential change: an administrative password reset and
/// a self-service password change both rotate the stamp, so every token
/// issued before the change — on every device, including a Genesys Screen
/// Pop session — stops passing authorization at once, while the user signs
/// in again and receives a token carrying the new stamp. A token with no
/// <c>sst</c> claim at all (one minted before this check existed) is
/// refused the same way: the outcome is the same 403 a deactivated
/// employee's token gets, never a hint about which check failed.
/// </para>
/// </summary>
public sealed class ActiveEmployeeHandler(IEmployeeDirectory employeeDirectory, IUserAccountManager accountManager)
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

        var tokenStamp = context.User.FindFirstValue(TigerCsTokenClaims.SecurityStamp);
        if (string.IsNullOrEmpty(tokenStamp))
        {
            return;
        }

        if (!await employeeDirectory.IsActiveAsync(employeeId))
        {
            return;
        }

        var currentStamp = await accountManager.GetSecurityStampAsync(employeeId);
        if (currentStamp is not null && string.Equals(currentStamp, tokenStamp, StringComparison.Ordinal))
        {
            context.Succeed(requirement);
        }
    }
}

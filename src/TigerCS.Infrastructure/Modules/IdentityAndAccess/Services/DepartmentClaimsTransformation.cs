using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;

/// <summary>
/// Adds the authenticated employee's current department-assignment claims on
/// every request, queried fresh from the database — never taken from the
/// JWT itself. This is what Security-Architecture.md §3 means by "evaluated
/// against the ticket's current data, not cached or assumed from the user's
/// session alone": a long-lived token's baked-in claims would go stale if a
/// department transfer happens mid-session; re-querying per request does not.
/// </summary>
public sealed class DepartmentClaimsTransformation(IEmployeeDirectory employeeDirectory) : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return principal;
        }

        if (principal.HasClaim(c => c.Type == TigerCsClaimTypes.DepartmentId))
        {
            // Already transformed earlier in this request's pipeline.
            return principal;
        }

        var idValue = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (idValue is null || !Guid.TryParse(idValue, out var employeeId))
        {
            return principal;
        }

        var assignments = await employeeDirectory.GetDepartmentAssignmentsAsync(employeeId);
        var identity = (ClaimsIdentity)principal.Identity;
        foreach (var assignment in assignments)
        {
            identity.AddClaim(new Claim(TigerCsClaimTypes.DepartmentId, assignment.DepartmentId.ToString()));
        }

        return principal;
    }
}

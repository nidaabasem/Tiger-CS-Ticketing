using Microsoft.AspNetCore.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

/// <summary>
/// Resource-based requirement: succeeds if the caller is assigned to the
/// target department (the resource, an int DepartmentId), or holds one of
/// the designated cross-department roles (Security-Architecture.md §3 —
/// CS-layer roles are scoped across all departments for certain actions).
/// </summary>
public sealed class DepartmentScopedRequirement(IReadOnlyCollection<string> crossDepartmentRoles)
    : IAuthorizationRequirement
{
    public IReadOnlyCollection<string> CrossDepartmentRoles { get; } = crossDepartmentRoles;
}

public sealed class DepartmentScopedHandler
    : AuthorizationHandler<DepartmentScopedRequirement, int>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, DepartmentScopedRequirement requirement, int resourceDepartmentId)
    {
        if (requirement.CrossDepartmentRoles.Any(context.User.IsInRole))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var hasDepartmentClaim = context.User.Claims.Any(c =>
            c.Type == Services.TigerCsClaimTypes.DepartmentId &&
            int.TryParse(c.Value, out var claimedDepartmentId) &&
            claimedDepartmentId == resourceDepartmentId);

        if (hasDepartmentClaim)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;

namespace TigerCS.Tests.IdentityAndAccess.Authorization;

public class DepartmentScopedHandlerTests
{
    private static AuthorizationHandlerContext CreateContext(
        int resourceDepartmentId, IReadOnlyCollection<int> memberOfDepartments, string? role,
        DepartmentScopedRequirement requirement)
    {
        var claims = memberOfDepartments
            .Select(id => new Claim(TigerCsClaimTypes.DepartmentId, id.ToString()))
            .ToList();
        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        return new AuthorizationHandlerContext([requirement], principal, resourceDepartmentId);
    }

    [Fact]
    public async Task MemberOfTargetDepartment_Succeeds()
    {
        var requirement = new DepartmentScopedRequirement([Roles.CsManager]);
        var context = CreateContext(resourceDepartmentId: 2, memberOfDepartments: [1, 2], role: Roles.DepartmentEmployee, requirement);
        var handler = new DepartmentScopedHandler();

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task NotMemberOfTargetDepartment_WithoutCrossDepartmentRole_DoesNotSucceed()
    {
        var requirement = new DepartmentScopedRequirement([Roles.CsManager]);
        var context = CreateContext(resourceDepartmentId: 5, memberOfDepartments: [1, 2], role: Roles.DepartmentEmployee, requirement);
        var handler = new DepartmentScopedHandler();

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task CrossDepartmentRole_SucceedsRegardlessOfMembership()
    {
        var requirement = new DepartmentScopedRequirement([Roles.CsManager]);
        var context = CreateContext(resourceDepartmentId: 99, memberOfDepartments: [], role: Roles.CsManager, requirement);
        var handler = new DepartmentScopedHandler();

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }
}

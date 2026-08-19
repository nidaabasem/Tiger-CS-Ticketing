using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Tests.IdentityAndAccess.Integration;

/// <summary>
/// Review item 6: proves the DepartmentScoped policy end-to-end through the
/// real, DI-registered IAuthorizationService, the real DepartmentScopedHandler,
/// and the real DepartmentClaimsTransformation (querying the live database) —
/// everything except the actual HTTP routing, since no controller endpoint in
/// this increment applies this policy yet (no Ticketing endpoint exists to
/// scope). Adding one would be a new feature, out of this review's scope;
/// this is the most complete test of the pipeline that's possible without one.
/// </summary>
public class DepartmentScopedAuthorizationTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public DepartmentScopedAuthorizationTests(TigerCsApiFactory factory) => _factory = factory;

    private async Task<ClaimsPrincipal> BuildTransformedPrincipalAsync(Guid employeeId, string role)
    {
        using var scope = _factory.Services.CreateScope();
        var transformation = scope.ServiceProvider.GetRequiredService<IClaimsTransformation>();

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, employeeId.ToString()), new Claim(ClaimTypes.Role, role)],
            "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        // Mirrors what actually happens per-request: claims are re-queried from
        // the database fresh every time, never taken from a cached/login-time
        // value — see DepartmentClaimsTransformation's own doc comment.
        return await transformation.TransformAsync(principal);
    }

    private static Task<AuthorizationResult> AuthorizeAsync(
        IServiceProvider services, ClaimsPrincipal principal, int resourceDepartmentId)
    {
        var authorizationService = services.GetRequiredService<IAuthorizationService>();
        return authorizationService.AuthorizeAsync(principal, resourceDepartmentId, PolicyNames.DepartmentScoped);
    }

    [Fact]
    public async Task DepartmentEmployee_OwnDepartment_IsAuthorized()
    {
        var (_, _, employeeId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);
        var deptA = await _factory.CreateDepartmentAsync("Dept A " + Guid.NewGuid(), "A" + Guid.NewGuid().ToString("N")[..6]);
        await _factory.AssignPrimaryDepartmentAsync(employeeId, deptA);

        var principal = await BuildTransformedPrincipalAsync(employeeId, Roles.DepartmentEmployee);

        using var scope = _factory.Services.CreateScope();
        var result = await AuthorizeAsync(scope.ServiceProvider, principal, deptA);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task DepartmentEmployee_FromDepartmentA_CannotAccessDepartmentB()
    {
        var (_, _, employeeId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);
        var deptA = await _factory.CreateDepartmentAsync("Dept A " + Guid.NewGuid(), "A" + Guid.NewGuid().ToString("N")[..6]);
        var deptB = await _factory.CreateDepartmentAsync("Dept B " + Guid.NewGuid(), "B" + Guid.NewGuid().ToString("N")[..6]);
        await _factory.AssignPrimaryDepartmentAsync(employeeId, deptA);

        var principal = await BuildTransformedPrincipalAsync(employeeId, Roles.DepartmentEmployee);

        using var scope = _factory.Services.CreateScope();
        var result = await AuthorizeAsync(scope.ServiceProvider, principal, deptB);

        Assert.False(result.Succeeded);
    }

    /// <summary>
    /// Review item 7's Supervisor requirement, and item 6's approved-matrix
    /// check: CS Supervisor is one of Security-Architecture.md §3's named
    /// cross-department roles and must be authorized for a department the
    /// supervisor has no membership in at all.
    /// </summary>
    [Fact]
    public async Task Supervisor_NoMembershipInTargetDepartment_IsStillAuthorized()
    {
        var (_, _, supervisorId) = await _factory.SeedEmployeeAsync(Roles.CsSupervisor);
        var deptB = await _factory.CreateDepartmentAsync("Dept B " + Guid.NewGuid(), "B" + Guid.NewGuid().ToString("N")[..6]);
        // Deliberately no AssignPrimaryDepartmentAsync call for this employee.

        var principal = await BuildTransformedPrincipalAsync(supervisorId, Roles.CsSupervisor);

        using var scope = _factory.Services.CreateScope();
        var result = await AuthorizeAsync(scope.ServiceProvider, principal, deptB);

        Assert.True(result.Succeeded);
    }

    /// <summary>
    /// Security-Architecture.md §3.1: Reporting User is scoped to
    /// reports/dashboards (Solution-Analysis.md §4.1), not included in
    /// DepartmentScoped's cross-department set — and has no department
    /// membership of its own to fall back on either.
    /// </summary>
    [Fact]
    public async Task ReportingUser_IsNotACrossDepartmentRole_CannotAccessAnyDepartment()
    {
        var (_, _, reportingUserId) = await _factory.SeedEmployeeAsync(Roles.ReportingUser);
        var deptA = await _factory.CreateDepartmentAsync("Dept A " + Guid.NewGuid(), "A" + Guid.NewGuid().ToString("N")[..6]);

        var principal = await BuildTransformedPrincipalAsync(reportingUserId, Roles.ReportingUser);

        using var scope = _factory.Services.CreateScope();
        var result = await AuthorizeAsync(scope.ServiceProvider, principal, deptA);

        Assert.False(result.Succeeded);
    }

    /// <summary>
    /// Security-Architecture.md §3.1: General Manager holds the broader,
    /// read-level cross-department grant (Solution-Analysis.md §4.1's
    /// "View: All tickets"), distinct from the CS-layer three's §3 basis.
    /// </summary>
    [Fact]
    public async Task GeneralManager_NoMembershipInTargetDepartment_IsStillAuthorized()
    {
        var (_, _, gmId) = await _factory.SeedEmployeeAsync(Roles.GeneralManager);
        var deptA = await _factory.CreateDepartmentAsync("Dept A " + Guid.NewGuid(), "A" + Guid.NewGuid().ToString("N")[..6]);

        var principal = await BuildTransformedPrincipalAsync(gmId, Roles.GeneralManager);

        using var scope = _factory.Services.CreateScope();
        var result = await AuthorizeAsync(scope.ServiceProvider, principal, deptA);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task DepartmentHead_IsNotACrossDepartmentRole_CannotAccessOtherDepartment()
    {
        var (_, _, headId) = await _factory.SeedEmployeeAsync(Roles.DepartmentHead);
        var deptA = await _factory.CreateDepartmentAsync("Dept A " + Guid.NewGuid(), "A" + Guid.NewGuid().ToString("N")[..6]);
        var deptB = await _factory.CreateDepartmentAsync("Dept B " + Guid.NewGuid(), "B" + Guid.NewGuid().ToString("N")[..6]);
        await _factory.AssignPrimaryDepartmentAsync(headId, deptA);

        var principal = await BuildTransformedPrincipalAsync(headId, Roles.DepartmentHead);

        using var scope = _factory.Services.CreateScope();
        var result = await AuthorizeAsync(scope.ServiceProvider, principal, deptB);

        Assert.False(result.Succeeded);
    }

    /// <summary>
    /// Review item 6's last bullet: department membership must not be trusted
    /// only from a claim supplied at login. Simulates the same underlying
    /// token/identity being evaluated twice — once before a department
    /// transfer, once after — without any new login in between, proving the
    /// second evaluation reflects the *current* assignment, not the first one.
    /// </summary>
    [Fact]
    public async Task DepartmentMembership_ChangedAfterFirstRequest_IsReflectedOnTheNextRequest_NoRelogin()
    {
        var (_, _, employeeId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);
        var deptA = await _factory.CreateDepartmentAsync("Dept A " + Guid.NewGuid(), "A" + Guid.NewGuid().ToString("N")[..6]);
        var deptB = await _factory.CreateDepartmentAsync("Dept B " + Guid.NewGuid(), "B" + Guid.NewGuid().ToString("N")[..6]);
        await _factory.AssignPrimaryDepartmentAsync(employeeId, deptA);

        // "Request 1": authorized for A, not yet for B.
        var principalAtRequest1 = await BuildTransformedPrincipalAsync(employeeId, Roles.DepartmentEmployee);
        using (var scope1 = _factory.Services.CreateScope())
        {
            Assert.True((await AuthorizeAsync(scope1.ServiceProvider, principalAtRequest1, deptA)).Succeeded);
            Assert.False((await AuthorizeAsync(scope1.ServiceProvider, principalAtRequest1, deptB)).Succeeded);
        }

        // Department transfer happens — no new login, no new token issued.
        await _factory.AssignPrimaryDepartmentAsync(employeeId, deptB);

        // "Request 2": a fresh principal built from the same underlying
        // identity claim (as a real second HTTP request would produce from
        // the same still-valid JWT) — the transformation re-queries live.
        var principalAtRequest2 = await BuildTransformedPrincipalAsync(employeeId, Roles.DepartmentEmployee);
        using (var scope2 = _factory.Services.CreateScope())
        {
            Assert.True((await AuthorizeAsync(scope2.ServiceProvider, principalAtRequest2, deptB)).Succeeded);
        }
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using TigerCS.Application.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Tests.IdentityAndAccess.Authorization;

/// <summary>
/// Unit coverage for the two halves of the central authorization override
/// (ADR-0024): <see cref="SystemAdministratorOverrideHandler"/> at the
/// ASP.NET Core policy layer, and <see cref="AuthorizationGate"/> at the
/// application-service layer.
///
/// <para>
/// The endpoint-by-endpoint proof lives in
/// <c>SystemAdministratorEndpointAuthorizationTests</c>; these tests pin the
/// mechanism's own properties — in particular the two that make it an
/// override rather than a hole: it grants nothing to any other role, and it
/// does not satisfy an <see cref="IIdentityGateRequirement"/>.
/// </para>
/// </summary>
public class SystemAdministratorOverrideHandlerTests
{
    /// <summary>A requirement type that did not exist when the override was written — standing in for the future SLA/escalation/reporting/administration policies of the correction's requirement 1.</summary>
    private sealed class FuturePolicyRequirement : IAuthorizationRequirement;

    /// <summary>A future identity gate: opts out of the override by implementing the marker, exactly as ActiveEmployeeRequirement does.</summary>
    private sealed class FutureIdentityGateRequirement : IIdentityGateRequirement;

    private static AuthorizationHandlerContext CreateContext(
        IReadOnlyCollection<IAuthorizationRequirement> requirements, params string[] roles)
    {
        var claims = roles.Select(r => new Claim(ClaimTypes.Role, r)).ToList();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        return new AuthorizationHandlerContext(requirements, principal, resource: null);
    }

    [Fact]
    public async Task SystemAdministrator_SatisfiesEveryRoleRequirementInTheCatalog()
    {
        // Every role-based requirement the policy catalog builds today, none
        // of which lists System Administrator among its allowed roles.
        var requirements = new IAuthorizationRequirement[]
        {
            new RolesAuthorizationRequirement([Roles.CsSupervisor, Roles.CsManager]),
            new RolesAuthorizationRequirement([Roles.DepartmentHead, Roles.CsManager]),
            new RolesAuthorizationRequirement([Roles.CsAgent, Roles.CsSupervisor])
        };
        var context = CreateContext(requirements, Roles.SystemAdministrator);

        await new SystemAdministratorOverrideHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
        Assert.Empty(context.PendingRequirements);
    }

    [Fact]
    public async Task SystemAdministrator_SatisfiesARequirementTypeTheOverrideHasNeverSeen()
    {
        // Requirement 4 of the correction: a policy added later is covered
        // with no change to the override, the policy catalog, or any
        // controller.
        var requirement = new FuturePolicyRequirement();
        var context = CreateContext([requirement], Roles.SystemAdministrator);

        await new SystemAdministratorOverrideHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task SystemAdministrator_SatisfiesTheDepartmentScopedRequirement_ForADepartmentTheyDoNotBelongTo()
    {
        // Resource-based requirements run through the same context, so the
        // override reaches them too — without DepartmentScopedRequirement's
        // own cross-department role list having to name the role.
        var requirement = new DepartmentScopedRequirement([Roles.CsManager]);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, Roles.SystemAdministrator)], "TestAuth"));
        var context = new AuthorizationHandlerContext([requirement], principal, resource: 42);

        await new SystemAdministratorOverrideHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task SystemAdministrator_DoesNotSatisfyTheActiveEmployeeIdentityGate()
    {
        // Security-Architecture.md §14 / FR-ADM-02: a deactivated
        // administrator holding an unexpired token is still refused. This is
        // the carve-out that keeps "authorized for everything" from becoming
        // "authenticated as nobody".
        var requirement = new ActiveEmployeeRequirement();
        var context = CreateContext([requirement], Roles.SystemAdministrator);

        await new SystemAdministratorOverrideHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.Contains(requirement, context.PendingRequirements);
    }

    [Fact]
    public async Task SystemAdministrator_DoesNotSatisfyAFutureIdentityGate()
    {
        var requirement = new FutureIdentityGateRequirement();
        var context = CreateContext([requirement], Roles.SystemAdministrator);

        await new SystemAdministratorOverrideHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task SystemAdministrator_DoesNotSatisfyTheDenyAnonymousRequirement()
    {
        var requirement = new DenyAnonymousAuthorizationRequirement();
        var context = CreateContext([requirement], Roles.SystemAdministrator);

        await new SystemAdministratorOverrideHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task SystemAdministrator_SatisfiesPermissionRequirements_ButLeavesTheIdentityGatePending()
    {
        // The mixed case a real policy actually presents: Base() adds
        // ActiveEmployeeRequirement to every policy alongside its role
        // requirement. The role part is overridden; the gate is not, so the
        // policy as a whole still fails until ActiveEmployeeHandler succeeds
        // it on its own terms.
        var roleRequirement = new RolesAuthorizationRequirement([Roles.CsManager]);
        var gate = new ActiveEmployeeRequirement();
        var context = CreateContext([roleRequirement, gate], Roles.SystemAdministrator);

        await new SystemAdministratorOverrideHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.DoesNotContain(roleRequirement, context.PendingRequirements);
        Assert.Contains(gate, context.PendingRequirements);
    }

    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.DepartmentHead)]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.ChairmanCeo)]
    [InlineData(Roles.ReportingUser)]
    public async Task EveryOtherRole_GetsNothingFromTheOverride(string role)
    {
        var requirement = new RolesAuthorizationRequirement([Roles.SystemAdministrator]);
        var context = CreateContext([requirement], role);

        await new SystemAdministratorOverrideHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task AnonymousPrincipal_GetsNothingFromTheOverride()
    {
        // No identity at all: no role claims to read, and the handler's own
        // IsAuthenticated check refuses before it looks.
        var context = new AuthorizationHandlerContext(
            [new RolesAuthorizationRequirement([Roles.SystemAdministrator])],
            new ClaimsPrincipal(new ClaimsIdentity()),
            resource: null);

        await new SystemAdministratorOverrideHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task UnauthenticatedPrincipalCarryingTheRoleClaim_GetsNothingFromTheOverride()
    {
        // A role claim on an identity with no authentication type is not an
        // authenticated identity — forging one must not reach the override.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, Roles.SystemAdministrator)]));
        var context = new AuthorizationHandlerContext(
            [new RolesAuthorizationRequirement([Roles.SystemAdministrator])], principal, resource: null);

        await new SystemAdministratorOverrideHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    // ---- Application-layer half: AuthorizationGate ----

    [Fact]
    public void Gate_SystemAdministrator_IsAuthorizedWithoutConsultingTheRule()
    {
        var ruleWasEvaluated = false;

        var authorized = AuthorizationGate.Evaluate([Roles.SystemAdministrator], () =>
        {
            ruleWasEvaluated = true;
            return false;
        });

        Assert.True(authorized);
        // The rule is skipped entirely, so an overridden caller costs no
        // department-membership round trip on the async path.
        Assert.False(ruleWasEvaluated);
    }

    [Fact]
    public async Task GateAsync_SystemAdministrator_IsAuthorizedWithoutAwaitingTheRule()
    {
        var ruleWasEvaluated = false;

        var authorized = await AuthorizationGate.EvaluateAsync([Roles.SystemAdministrator], () =>
        {
            ruleWasEvaluated = true;
            return Task.FromResult(false);
        });

        Assert.True(authorized);
        Assert.False(ruleWasEvaluated);
    }

    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.DepartmentHead)]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.ChairmanCeo)]
    [InlineData(Roles.ReportingUser)]
    public async Task Gate_EveryOtherRole_FallsThroughToTheServicesOwnRule(string role)
    {
        Assert.False(AuthorizationGate.Evaluate([role], () => false));
        Assert.True(AuthorizationGate.Evaluate([role], () => true));
        Assert.False(await AuthorizationGate.EvaluateAsync([role], () => Task.FromResult(false)));
        Assert.True(await AuthorizationGate.EvaluateAsync([role], () => Task.FromResult(true)));
    }

    [Fact]
    public void Override_MatchesTheRoleNameOrdinally_NotCaseInsensitively()
    {
        // AspNetRoles.Name is what the JWT role claim carries verbatim; a
        // case-insensitive match here would authorize a role name the
        // identity store treats as a different role.
        Assert.True(AuthorizationOverride.AppliesTo([Roles.SystemAdministrator]));
        Assert.False(AuthorizationOverride.AppliesTo(["system administrator"]));
        Assert.False(AuthorizationOverride.AppliesTo(["SYSTEM ADMINISTRATOR"]));
        Assert.False(AuthorizationOverride.AppliesTo([]));
    }

    [Fact]
    public void Override_NamesExactlyOneRole_AndItIsSystemAdministrator()
    {
        // Requirement 3: the administrator account holds only "System
        // Administrator" — no CS Agent or other role is granted anywhere, and
        // no second role overrides.
        Assert.Equal(Roles.SystemAdministrator, AuthorizationOverride.OverrideRole);

        foreach (var role in Roles.All.Where(r => r != Roles.SystemAdministrator))
        {
            Assert.False(AuthorizationOverride.AppliesTo([role]));
        }
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Tests.IdentityAndAccess.Authorization;

public class PolicyCatalogTests
{
    private static AuthorizationOptions BuildOptions()
    {
        var options = new AuthorizationOptions();
        options.AddTigerCsAuthorizationPolicies();
        return options;
    }

    private static IReadOnlyCollection<string> RolesOf(AuthorizationPolicy policy) =>
        policy.Requirements.OfType<RolesAuthorizationRequirement>().SelectMany(r => r.AllowedRoles).ToList();

    [Theory]
    [InlineData(PolicyNames.SupervisorOrAbove, new[] { Roles.CsSupervisor, Roles.CsManager, Roles.GeneralManager, Roles.ChairmanCeo })]
    [InlineData(PolicyNames.DepartmentHeadOrAbove, new[] { Roles.DepartmentHead, Roles.CsManager, Roles.GeneralManager, Roles.ChairmanCeo })]
    [InlineData(PolicyNames.CsManagerOrGeneralManager, new[] { Roles.CsManager, Roles.GeneralManager, Roles.ChairmanCeo })]
    [InlineData(PolicyNames.SystemAdministrator, new[] { Roles.SystemAdministrator })]
    public void NamedPolicy_HasExactlyTheExpectedRoleSet(string policyName, string[] expectedRoles)
    {
        var options = BuildOptions();
        var policy = options.GetPolicy(policyName);

        Assert.NotNull(policy);
        Assert.Equal(expectedRoles.OrderBy(r => r), RolesOf(policy!).OrderBy(r => r));
    }

    [Fact]
    public void EveryTieredPolicy_AlsoRequiresActiveEmployee()
    {
        var options = BuildOptions();

        foreach (var name in new[]
                 {
                     PolicyNames.AuthenticatedStaff, PolicyNames.SupervisorOrAbove,
                     PolicyNames.DepartmentHeadOrAbove, PolicyNames.CsManagerOrGeneralManager,
                     PolicyNames.SystemAdministrator
                 })
        {
            var policy = options.GetPolicy(name);
            Assert.NotNull(policy);
            Assert.Contains(policy!.Requirements, r => r is ActiveEmployeeRequirement);
        }
    }

    [Fact]
    public void DepartmentScopedPolicy_HasTheDesignatedCrossDepartmentRoles()
    {
        var options = BuildOptions();
        var policy = options.GetPolicy(PolicyNames.DepartmentScoped);

        Assert.NotNull(policy);
        var requirement = policy!.Requirements.OfType<DepartmentScopedRequirement>().Single();
        Assert.Equal(
            new[] { Roles.CsSupervisor, Roles.CsManager, Roles.GeneralManager, Roles.ChairmanCeo, Roles.SystemAdministrator }
                .OrderBy(r => r),
            requirement.CrossDepartmentRoles.OrderBy(r => r));
    }
}

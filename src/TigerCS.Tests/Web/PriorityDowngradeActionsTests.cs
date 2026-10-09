// TigerCS.Web is referenced under an alias - see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCsWeb::TigerCS.Web.Models;

namespace TigerCS.Tests.Web;

/// <summary>The Ticket Details downgrade controls mirror the Api's own rule (PriorityDowngradeAppService).</summary>
public class PriorityDowngradeActionsTests
{
    private static TicketActionContext Context(string role, bool inDepartment = true, bool owner = false)
    {
        var viewer = Guid.NewGuid();
        return new TicketActionContext(
            [role], viewer, owner ? viewer : Guid.NewGuid(), CurrentDepartmentId: 2, inDepartment ? [2] : [9]);
    }

    [Fact]
    public void ADepartmentHeadOfTheTicketsDepartment_CanDecide_ButAnotherDepartmentsHeadCannot()
    {
        Assert.True(TicketActions.CanDecidePriorityDowngrade(Context(Roles.DepartmentHead)));
        Assert.False(TicketActions.CanDecidePriorityDowngrade(Context(Roles.DepartmentHead, inDepartment: false)));
    }

    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.CsSupervisor)]
    public void OrdinaryStaffCannotDecide(string role) =>
        Assert.False(TicketActions.CanDecidePriorityDowngrade(Context(role)));

    [Theory]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.SystemAdministrator)]
    public void TheAboveTierAndTheOverrideCanDecide(string role) =>
        Assert.True(TicketActions.CanDecidePriorityDowngrade(Context(role, inDepartment: false)));

    [Fact]
    public void AgentsAndTheTicketOwnerCanRequest_AnOutsiderDepartmentEmployeeCannot()
    {
        Assert.True(TicketActions.CanRequestPriorityDowngrade(Context(Roles.CsAgent)));
        Assert.True(TicketActions.CanRequestPriorityDowngrade(Context(Roles.DepartmentEmployee, owner: true)));
        Assert.True(TicketActions.CanRequestPriorityDowngrade(Context(Roles.DepartmentEmployee)));
        Assert.False(TicketActions.CanRequestPriorityDowngrade(Context(Roles.DepartmentEmployee, inDepartment: false)));
        Assert.False(TicketActions.CanRequestPriorityDowngrade(null));
    }
}

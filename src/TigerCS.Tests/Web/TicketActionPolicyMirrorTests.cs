using System.Reflection;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Web.Authorization;

namespace TigerCS.Tests.Web;

/// <summary>
/// Keeps the UI's action policy honest against the server's own role sets.
///
/// <para>
/// <see cref="TicketActionPolicy"/> duplicates the server's rules so the action
/// bar shows what a user can actually use. Duplication drifts, so these tests
/// read <see cref="TicketRoleSets"/> - the server's real constants, not a
/// second copy of them - and assert the UI agrees. If the permission matrix
/// changes server-side, these fail rather than the UI quietly offering an
/// action that 403s, or hiding one the user is entitled to.
/// </para>
///
/// <para>
/// This is not a claim that the UI enforces anything. It does not; the API
/// does. It is a claim that the UI's guess matches the API's answer.
/// </para>
/// </summary>
public sealed class TicketActionPolicyMirrorTests
{
    private static readonly Guid SelfId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static UserContext User(string role, params int[] departmentIds) =>
        new(SelfId, "Test User", [role], departmentIds.ToDictionary(id => id, id => $"Department {id}"));

    [Fact]
    public void Transfer_MatchesTheServersTransferRoleSetExactly()
    {
        foreach (var role in Roles.All)
        {
            var expected = TicketRoleSets.Transfer.Contains(role) || role == Roles.SystemAdministrator;
            var actual = TicketActionPolicy.CanTransfer(User(role, 1), TestData.Detail());

            Assert.True(
                expected == actual,
                $"Transfer for '{role}': server says {expected}, UI says {actual}.");
        }
    }

    [Fact]
    public void Close_MatchesTheServersCloseRoleSetExactly()
    {
        foreach (var role in Roles.All)
        {
            var expected = TicketRoleSets.Close.Contains(role) || role == Roles.SystemAdministrator;
            var actual = TicketActionPolicy.CanClose(User(role, 1), TestData.Detail());

            Assert.True(
                expected == actual,
                $"Close for '{role}': server says {expected}, UI says {actual}.");
        }
    }

    [Fact]
    public void Resolve_IsOfferedOnlyToTheServersResolveRoles()
    {
        foreach (var role in Roles.All)
        {
            // A Department Head in the ticket's department, or a Department
            // Employee who owns it - the two shapes the server's rule allows.
            var head = TicketActionPolicy.CanResolve(User(role, 1), TestData.Detail());
            var owner = TicketActionPolicy.CanResolve(User(role, 1), TestData.Detail(owner: SelfId));

            var expected = TicketRoleSets.Resolve.Contains(role) || role == Roles.SystemAdministrator;

            Assert.True(
                expected == (head || owner),
                $"Resolve for '{role}': server says {expected}, UI says {head || owner}.");
        }
    }

    [Fact]
    public void Assign_RespectsTheDepartmentScopeTheServerApplies()
    {
        // CS Supervisor and Department Head may assign only inside a department
        // they belong to; CS Manager may assign anywhere.
        foreach (var role in TicketRoleSets.AssignWithinOwnDepartment)
        {
            Assert.True(
                TicketActionPolicy.CanAssign(User(role, 1), TestData.Detail(departmentId: 1)),
                $"'{role}' should be able to assign inside their own department.");

            Assert.False(
                TicketActionPolicy.CanAssign(User(role, 1), TestData.Detail(departmentId: 99)),
                $"'{role}' should not be able to assign in a department they do not belong to.");
        }

        foreach (var role in TicketRoleSets.AssignCrossDepartment)
        {
            Assert.True(
                TicketActionPolicy.CanAssign(User(role, 1), TestData.Detail(departmentId: 99)),
                $"'{role}' should be able to assign across departments.");
        }
    }

    [Fact]
    public void Assign_IsOfferedToNobodyOutsideTheTwoAssignmentRoleSets()
    {
        var assignmentRoles = TicketRoleSets.AssignWithinOwnDepartment
            .Concat(TicketRoleSets.AssignCrossDepartment)
            .Append(Roles.SystemAdministrator)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var role in Roles.All.Where(r => !assignmentRoles.Contains(r)))
        {
            Assert.False(
                TicketActionPolicy.CanAssign(User(role, 1), TestData.Detail(departmentId: 1)),
                $"'{role}' holds no assignment capability server-side and must not be offered one.");
        }
    }

    [Fact]
    public void ChangeStatus_IsOfferedToTheOwnerAndToTheServersSupervisoryRoles()
    {
        // The ticket's current owner, whatever their role.
        Assert.True(TicketActionPolicy.CanChangeStatus(
            User(Roles.DepartmentEmployee, 1), TestData.Detail(owner: SelfId)));

        // A cross-department supervisory role, on any ticket.
        foreach (var role in TicketRoleSets.CrossDepartmentSupervisory)
        {
            Assert.True(
                TicketActionPolicy.CanChangeStatus(User(role, 1), TestData.Detail(owner: OtherId, departmentId: 99)),
                $"'{role}' is cross-department supervisory server-side.");
        }

        // Department Head is department-scoped, not cross-department.
        Assert.True(TicketActionPolicy.CanChangeStatus(
            User(Roles.DepartmentHead, 1), TestData.Detail(owner: OtherId, departmentId: 1)));
        Assert.False(TicketActionPolicy.CanChangeStatus(
            User(Roles.DepartmentHead, 1), TestData.Detail(owner: OtherId, departmentId: 99)));
    }

    [Fact]
    public void SystemAdministrator_PassesEveryActionWithoutAppearingInAnyRoleSet()
    {
        // ADR-0024's central override, and the reason it is worth asserting:
        // the role is deliberately absent from every operational role set and
        // is authorized anyway.
        foreach (var roleSet in new[]
        {
            TicketRoleSets.AssignWithinOwnDepartment,
            TicketRoleSets.AssignCrossDepartment,
            TicketRoleSets.Transfer,
            TicketRoleSets.Resolve,
            TicketRoleSets.Close
        })
        {
            Assert.DoesNotContain(Roles.SystemAdministrator, roleSet);
        }

        var admin = User(Roles.SystemAdministrator);
        var ticket = TestData.Detail(verification: "PendingCrmVerification");

        Assert.True(TicketActionPolicy.CanAssign(admin, ticket));
        Assert.True(TicketActionPolicy.CanTransfer(admin, ticket));
        Assert.True(TicketActionPolicy.CanChangeStatus(admin, ticket));
        Assert.True(TicketActionPolicy.CanResolve(admin, ticket));
        Assert.True(TicketActionPolicy.CanClose(admin, ticket));
        Assert.True(TicketActionPolicy.CanReconcile(admin, ticket));
        Assert.True(TicketActionPolicy.CanCreateTickets(admin));
    }

    [Fact]
    public void EveryAction_IsRefusedOnAClosedTicket()
    {
        // Closed-ticket immutability is a server rule (422 on all of them);
        // this asserts the UI never offers any of them anyway.
        var closed = TestData.Detail(status: "Closed", verification: "PendingCrmVerification", owner: SelfId);

        foreach (var role in Roles.All)
        {
            var user = User(role, 1);

            Assert.False(TicketActionPolicy.CanAssign(user, closed), $"Assign offered to '{role}' on a closed ticket.");
            Assert.False(TicketActionPolicy.CanTransfer(user, closed), $"Transfer offered to '{role}' on a closed ticket.");
            Assert.False(TicketActionPolicy.CanChangeStatus(user, closed), $"Status change offered to '{role}' on a closed ticket.");
            Assert.False(TicketActionPolicy.CanResolve(user, closed), $"Resolve offered to '{role}' on a closed ticket.");
            Assert.False(TicketActionPolicy.CanClose(user, closed), $"Close offered to '{role}' on a closed ticket.");
            Assert.False(TicketActionPolicy.CanReconcile(user, closed), $"Reconcile offered to '{role}' on a closed ticket.");
        }

        Assert.False(TicketActionPolicy.CanAddNote(closed));
    }

    [Fact]
    public void CreateTickets_MatchesTheApisCustomerVerificationPolicy()
    {
        // The API's CustomerVerification policy is CS Agent and CS Supervisor,
        // plus the System Administrator override.
        string[] permitted = [Roles.CsAgent, Roles.CsSupervisor, Roles.SystemAdministrator];

        foreach (var role in Roles.All)
        {
            Assert.True(
                permitted.Contains(role) == TicketActionPolicy.CanCreateTickets(User(role)),
                $"Ticket creation for '{role}' does not match the API's policy.");
        }
    }

    [Fact]
    public void TicketActionPolicy_ContainsNoStateOfItsOwn()
    {
        // A permission helper that caches anything is a permission helper that
        // can answer for the wrong user. This one is a pure function of its
        // arguments, and stays that way.
        var fields = typeof(TicketActionPolicy)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => !f.IsLiteral)
            .ToArray();

        Assert.Empty(fields);
    }
}

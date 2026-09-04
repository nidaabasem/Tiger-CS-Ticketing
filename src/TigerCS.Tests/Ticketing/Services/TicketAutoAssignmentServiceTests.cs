using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// The Department + Request Type → assignment automation: configured rules
/// are respected, nothing valid falls through to a random employee, and
/// every outcome — assignment or department queue — is audited as a system
/// action, never as if a person performed it.
/// </summary>
public class TicketAutoAssignmentServiceTests
{
    private const int DepartmentId = 2;
    private static readonly DateTime Now = new(2026, 9, 4, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Fixture(
        TicketAutoAssignmentService Service,
        FakeTicketRepository Tickets,
        FakeRequestTypeAssignmentRuleRepository Rules,
        FakeDepartmentWorkflowSettingsRepository Settings,
        FakeUserDepartmentAssignmentRepository DepartmentAssignments,
        FakeTicketAssignmentRepository TicketAssignments,
        FakeAuditEntryWriter Audit);

    private static Fixture CreateService()
    {
        var tickets = new FakeTicketRepository();
        var rules = new FakeRequestTypeAssignmentRuleRepository();
        var settings = new FakeDepartmentWorkflowSettingsRepository();
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository();
        var ticketAssignments = new FakeTicketAssignmentRepository();
        var audit = new FakeAuditEntryWriter();

        var service = new TicketAutoAssignmentService(rules, settings, departmentAssignments, ticketAssignments, audit);
        return new Fixture(service, tickets, rules, settings, departmentAssignments, ticketAssignments, audit);
    }

    private static async Task<Ticket> SeedClassifiedTicketAsync(Fixture f, int requestTypeId)
    {
        var ticket = Ticket.CreateUnverified(
            $"TG-FM-20260904-{requestTypeId:D4}", DepartmentId, categoryId: 5,
            (byte)PriorityLevel.Medium, "AC not cooling", Now);
        await f.Tickets.AddAsync(ticket);
        ticket.ClassifyRequestType(requestTypeId);
        return ticket;
    }

    private static Guid SeedDepartmentMember(Fixture f, int departmentId = DepartmentId)
    {
        var employeeId = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(employeeId, departmentId, isPrimary: true, Now, assignedByEmployeeId: null));
        return employeeId;
    }

    [Fact]
    public async Task SpecificEmployeeRule_AssignsThePrimary_AsASystemAction()
    {
        var f = CreateService();
        var ticket = await SeedClassifiedTicketAsync(f, requestTypeId: 10);
        var employee = SeedDepartmentMember(f);
        f.Rules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(10, employee));

        var result = await f.Service.ApplyAsync(ticket, Now, Guid.NewGuid());

        Assert.Equal(AutoAssignmentOutcome.AssignedToPrimary, result.Outcome);
        Assert.Equal(employee, ticket.CurrentOwnerEmployeeId);

        // The assignment row and the audit entry are system actions — no
        // human actor is recorded, so history can never read as if the
        // creating agent assigned it.
        var assignment = Assert.Single(f.TicketAssignments.Added);
        Assert.Null(assignment.AssigningActorEmployeeId);
        Assert.Equal(employee, assignment.AssignedEmployeeId);
        Assert.True(assignment.IsCurrent);

        var audit = Assert.Single(f.Audit.Written, w => w.Action == "AutoAssign");
        Assert.Null(audit.ActorEmployeeId);
    }

    [Fact]
    public async Task NoConfiguredRule_LeavesTheTicketInTheDepartmentQueue_NeverARandomEmployee()
    {
        var f = CreateService();
        var ticket = await SeedClassifiedTicketAsync(f, requestTypeId: 11);
        SeedDepartmentMember(f); // a member exists — and must NOT be picked

        var result = await f.Service.ApplyAsync(ticket, Now, Guid.NewGuid());

        Assert.Equal(AutoAssignmentOutcome.NoRuleConfigured, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Empty(f.TicketAssignments.Added);

        // The queue outcome is itself audited as a system action.
        var audit = Assert.Single(f.Audit.Written, w => w.Action == "AutoAssign");
        Assert.Null(audit.ActorEmployeeId);
    }

    [Fact]
    public async Task TicketWithoutRequestType_IsUntouched()
    {
        var f = CreateService();
        var ticket = Ticket.CreateUnverified("TG-FM-20260904-0001", DepartmentId, 5, (byte)PriorityLevel.Medium, "AC", Now);
        await f.Tickets.AddAsync(ticket);

        var result = await f.Service.ApplyAsync(ticket, Now, Guid.NewGuid());

        Assert.Equal(AutoAssignmentOutcome.NoRequestType, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Empty(f.Audit.Written);
    }

    [Fact]
    public async Task TeamRule_AssignsThePrimaryAsSingleOwner_AndSurfacesTheMembers()
    {
        var f = CreateService();
        var ticket = await SeedClassifiedTicketAsync(f, requestTypeId: 12);
        var primary = SeedDepartmentMember(f);
        var memberA = Guid.NewGuid();
        var memberB = Guid.NewGuid();
        f.Rules.Add(RequestTypeAssignmentRule.ForTeam(12, primary, [memberA, memberB], "AC Team"));

        var result = await f.Service.ApplyAsync(ticket, Now, Guid.NewGuid());

        Assert.Equal(AutoAssignmentOutcome.AssignedToPrimary, result.Outcome);

        // One accountable owner — the primary; members are configuration,
        // never competing owners.
        Assert.Equal(primary, ticket.CurrentOwnerEmployeeId);
        Assert.Equal([memberA, memberB], result.TeamMemberEmployeeIds);
        Assert.Single(f.TicketAssignments.Added);
    }

    [Fact]
    public async Task ConfiguredAssigneeOutsideTheDepartment_FallsBackToTheQueue()
    {
        var f = CreateService();
        var ticket = await SeedClassifiedTicketAsync(f, requestTypeId: 13);
        var outsider = SeedDepartmentMember(f, departmentId: 99); // member of another department
        f.Rules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(13, outsider));

        var result = await f.Service.ApplyAsync(ticket, Now, Guid.NewGuid());

        Assert.Equal(AutoAssignmentOutcome.ConfiguredAssigneeNotInDepartment, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Empty(f.TicketAssignments.Added);
        Assert.Single(f.Audit.Written, w => w.Action == "AutoAssign");
    }

    [Fact]
    public async Task DepartmentSettingsDisablingAssignment_ForceTheQueue()
    {
        var f = CreateService();
        var ticket = await SeedClassifiedTicketAsync(f, requestTypeId: 14);
        var employee = SeedDepartmentMember(f);
        f.Rules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(14, employee));
        f.Settings.Add(new DepartmentWorkflowSettings(
            DepartmentId, allowAssignment: false, allowInternalReassignment: false, allowTransferToOtherDepartments: true));

        var result = await f.Service.ApplyAsync(ticket, Now, Guid.NewGuid());

        Assert.Equal(AutoAssignmentOutcome.AssignmentDisabledForDepartment, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task DifferentRequestTypesInTheSameDepartment_ResolveDifferentRules()
    {
        var f = CreateService();
        var nocOfficer = SeedDepartmentMember(f);

        // Request type 20: fixed officer. Request type 21: department queue.
        f.Rules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(20, nocOfficer));
        f.Rules.Add(RequestTypeAssignmentRule.ForDepartmentQueue(21));

        var officerTicket = await SeedClassifiedTicketAsync(f, requestTypeId: 20);
        var queueTicket = await SeedClassifiedTicketAsync(f, requestTypeId: 21);

        var officerResult = await f.Service.ApplyAsync(officerTicket, Now, Guid.NewGuid());
        var queueResult = await f.Service.ApplyAsync(queueTicket, Now, Guid.NewGuid());

        Assert.Equal(AutoAssignmentOutcome.AssignedToPrimary, officerResult.Outcome);
        Assert.Equal(nocOfficer, officerTicket.CurrentOwnerEmployeeId);
        Assert.Equal(AutoAssignmentOutcome.DepartmentQueueByRule, queueResult.Outcome);
        Assert.Null(queueTicket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task InactiveRule_BehavesLikeNoRule()
    {
        var f = CreateService();
        var ticket = await SeedClassifiedTicketAsync(f, requestTypeId: 15);
        var employee = SeedDepartmentMember(f);
        f.Rules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(15, employee, isActive: false));

        var result = await f.Service.ApplyAsync(ticket, Now, Guid.NewGuid());

        Assert.Equal(AutoAssignmentOutcome.NoRuleConfigured, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
    }
}

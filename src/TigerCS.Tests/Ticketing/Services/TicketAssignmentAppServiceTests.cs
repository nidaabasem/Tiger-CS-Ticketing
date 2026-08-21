using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

public class TicketAssignmentAppServiceTests
{
    private sealed record Fixture(
        TicketAssignmentAppService Service,
        FakeTicketRepository Tickets,
        FakeTicketAssignmentRepository Assignments,
        FakeUserDepartmentAssignmentRepository DepartmentAssignments,
        FakeDepartmentRepository Departments,
        FakeAuditEntryWriter Audit,
        FakeTicketingUnitOfWork UnitOfWork);

    private static Fixture CreateService()
    {
        var tickets = new FakeTicketRepository();
        var assignments = new FakeTicketAssignmentRepository();
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository();
        var departments = new FakeDepartmentRepository();
        var audit = new FakeAuditEntryWriter();
        var unitOfWork = new FakeTicketingUnitOfWork();

        var service = new TicketAssignmentAppService(
            tickets, assignments, departmentAssignments, departments, unitOfWork, audit, TimeProvider.System);

        return new Fixture(service, tickets, assignments, departmentAssignments, departments, audit, unitOfWork);
    }

    private static async Task<Ticket> SeedTicketAsync(FakeTicketRepository repo, int departmentId = 2)
    {
        var ticket = Ticket.CreateVerified(
            "TG-CS-20260821-0001", departmentId, unitReferenceId: 10, contactReferenceId: 20,
            categoryId: 5, priorityId: (byte)PriorityLevel.High, "AC not cooling", DateTime.UtcNow);
        await repo.AddAsync(ticket);
        return ticket;
    }

    private static async Task<Ticket> SeedClosedTicketAsync(FakeTicketRepository repo, int departmentId = 2)
    {
        var ticket = await SeedTicketAsync(repo, departmentId);
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        ticket.Close();
        return ticket;
    }

    // ---- Assignment authorization matrix (PR correction) — all 9 roles ----
    // CS Agent and Department Employee: no assignment capability at all
    // (not even self-claim, which this correction removes entirely).
    // CS Supervisor / Department Head: assign/reassign within their own
    // department only. CS Manager: cross-department. GM/Chairman/Reporting
    // User: no operational assignment.
    // System Administrator: authorized — not by the permission matrix, which
    // still grants it no Assign, but by the ADR-0024 central override, which
    // TicketAssignmentAppService reaches through AuthorizationGate. This row
    // is the superseded exclusion's replacement, not a widened matrix.
    public static IEnumerable<object[]> AssignRoleMatrix() =>
    [
        [Roles.CsAgent, false],
        [Roles.DepartmentEmployee, false],
        [Roles.CsSupervisor, true],
        [Roles.DepartmentHead, true],
        [Roles.CsManager, true],
        [Roles.GeneralManager, false],
        [Roles.ChairmanCeo, false],
        [Roles.SystemAdministrator, true],
        [Roles.ReportingUser, false]
    ];

    [Theory]
    [MemberData(nameof(AssignRoleMatrix))]
    public async Task AssignAsync_AllNineRoles_MatchesCorrectedAuthorizationMatrix(string role, bool expectSuccess)
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var caller = Guid.NewGuid();
        var target = Guid.NewGuid();
        // Caller and target both belong to the ticket's department — isolates
        // the role check itself from department-scoping (covered separately
        // below), except for CS Manager, whose authority doesn't depend on it.
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(caller, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(target, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));

        var result = await f.Service.AssignAsync(
            caller, [role], ticket.TicketId, new AssignTicketRequestDto(target, []));

        Assert.Equal(expectSuccess ? TicketMutationOutcome.Success : TicketMutationOutcome.Forbidden, result.Outcome);
        if (expectSuccess)
        {
            Assert.Equal(target, ticket.CurrentOwnerEmployeeId);
        }
        else
        {
            Assert.Null(ticket.CurrentOwnerEmployeeId);
            Assert.Equal(0, f.UnitOfWork.SaveChangesCallCount);
        }
    }

    [Fact]
    public async Task AssignAsync_SupervisorOfDifferentDepartment_ReturnsForbidden_DepartmentScoped()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        var caller = Guid.NewGuid();
        var target = Guid.NewGuid();
        // Supervisor, but for department 999, not the ticket's department 2.
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(caller, 999, true, DateTime.UtcNow, null));
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(target, 2, true, DateTime.UtcNow, null));

        var result = await f.Service.AssignAsync(
            caller, [Roles.CsSupervisor], ticket.TicketId, new AssignTicketRequestDto(target, []));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task AssignAsync_ByDepartmentHeadOfDifferentDepartment_ReturnsForbidden_PreventsCrossDepartmentAssignment()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        var caller = Guid.NewGuid();
        var target = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(caller, 999, true, DateTime.UtcNow, null));
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(target, 2, true, DateTime.UtcNow, null));

        var result = await f.Service.AssignAsync(
            caller, [Roles.DepartmentHead], ticket.TicketId, new AssignTicketRequestDto(target, []));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task AssignAsync_CsManagerAcrossDepartments_Succeeds_NoOwnDepartmentMembershipRequired()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        var caller = Guid.NewGuid(); // CS Manager, member of no department at all.
        var target = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(target, 2, true, DateTime.UtcNow, null));

        var result = await f.Service.AssignAsync(
            caller, [Roles.CsManager], ticket.TicketId, new AssignTicketRequestDto(target, []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task AssignAsync_AssignedEmployeeNotInTicketDepartment_ReturnsEmployeeNotInDepartment()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var caller = Guid.NewGuid();
        var target = Guid.NewGuid();
        // CS Manager (cross-department authority, isolates this check from the caller's own department scoping).
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(target, 999, true, DateTime.UtcNow, null));

        var result = await f.Service.AssignAsync(
            caller, [Roles.CsManager], ticket.TicketId, new AssignTicketRequestDto(target, []));

        Assert.Equal(TicketMutationOutcome.EmployeeNotInDepartment, result.Outcome);
    }

    [Fact]
    public async Task AssignAsync_OnClosedTicket_ReturnsTicketClosed_NoDatabaseWrites()
    {
        var f = CreateService();
        var ticket = await SeedClosedTicketAsync(f.Tickets);
        var caller = Guid.NewGuid();
        var target = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(target, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));

        var result = await f.Service.AssignAsync(
            caller, [Roles.CsManager], ticket.TicketId, new AssignTicketRequestDto(target, []));

        Assert.Equal(TicketMutationOutcome.TicketClosed, result.Outcome);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, f.UnitOfWork.SaveChangesCallCount);
        Assert.Empty(f.Assignments.Added);
        Assert.DoesNotContain(f.Audit.Written, w => w.Action == "Assign");
    }

    /// <summary>
    /// Requirement 2 of the ADR-0024 correction: the override grants
    /// authorization, not immunity. A System Administrator that loses a
    /// RowVersion race gets the same 409 outcome and the same rollback as
    /// the CS Supervisor in the test below — the authorization gate is
    /// passed, the concurrency check still refuses.
    /// </summary>
    [Fact]
    public async Task AssignAsync_SystemAdministrator_ConcurrentModification_StillReturnsConcurrencyConflictAndRollsBack()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var caller = Guid.NewGuid();
        var target = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(target, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));
        f.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = 1;

        var result = await f.Service.AssignAsync(
            caller, [Roles.SystemAdministrator], ticket.TicketId, new AssignTicketRequestDto(target, []));

        Assert.Equal(TicketMutationOutcome.ConcurrencyConflict, result.Outcome);
        Assert.Equal(1, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, f.UnitOfWork.TransactionsCommitted);
        Assert.Equal(1, f.UnitOfWork.TransactionsRolledBack);
    }

    /// <summary>
    /// The administrator is authorized for Assign but the ticket's own
    /// validation is untouched: the assignment target must still be a member
    /// of the ticket's current department (MVP-API-Contracts.md §3.5).
    /// </summary>
    [Fact]
    public async Task AssignAsync_SystemAdministrator_TargetOutsideTheTicketsDepartment_StillReturnsEmployeeNotInDepartment()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);

        var result = await f.Service.AssignAsync(
            Guid.NewGuid(), [Roles.SystemAdministrator], ticket.TicketId,
            new AssignTicketRequestDto(Guid.NewGuid(), []));

        Assert.Equal(TicketMutationOutcome.EmployeeNotInDepartment, result.Outcome);
        Assert.Equal(0, f.UnitOfWork.SaveChangesCallCount);
    }

    /// <summary>Closed-ticket immutability outranks the override: 422, not 200.</summary>
    [Fact]
    public async Task AssignAsync_SystemAdministrator_OnClosedTicket_StillReturnsTicketClosed_NoDatabaseWrites()
    {
        var f = CreateService();
        var ticket = await SeedClosedTicketAsync(f.Tickets);
        var target = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(target, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));

        var result = await f.Service.AssignAsync(
            Guid.NewGuid(), [Roles.SystemAdministrator], ticket.TicketId, new AssignTicketRequestDto(target, []));

        Assert.Equal(TicketMutationOutcome.TicketClosed, result.Outcome);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, f.UnitOfWork.SaveChangesCallCount);
        Assert.Empty(f.Assignments.Added);
    }

    [Fact]
    public async Task AssignAsync_ConcurrentModification_ReturnsConcurrencyConflictAndRollsBack()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var caller = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(caller, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));
        f.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = 1;

        var result = await f.Service.AssignAsync(
            caller, [Roles.CsSupervisor], ticket.TicketId, new AssignTicketRequestDto(caller, []));

        Assert.Equal(TicketMutationOutcome.ConcurrencyConflict, result.Outcome);
        Assert.Equal(1, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, f.UnitOfWork.TransactionsCommitted);
        Assert.Equal(1, f.UnitOfWork.TransactionsRolledBack);
    }

    // ---- Transfer authorization matrix (PR correction): CS Manager only,
    // plus System Administrator via the ADR-0024 central override ----
    public static IEnumerable<object[]> TransferRoleMatrix() =>
    [
        [Roles.CsAgent, false],
        [Roles.DepartmentEmployee, false],
        [Roles.CsSupervisor, false],
        [Roles.DepartmentHead, false],
        [Roles.CsManager, true],
        [Roles.GeneralManager, false],
        [Roles.ChairmanCeo, false],
        [Roles.SystemAdministrator, true],
        [Roles.ReportingUser, false]
    ];

    [Theory]
    [MemberData(nameof(TransferRoleMatrix))]
    public async Task TransferAsync_AllNineRoles_MatchesCorrectedAuthorizationMatrix(string role, bool expectSuccess)
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        ticket.AssignTo(Guid.NewGuid());
        var targetDepartment = f.Departments.AddDepartment("Facility Management " + role, "FM-" + role[..Math.Min(3, role.Length)]);
        var caller = Guid.NewGuid();
        // Even Department Head/Supervisor are department-scoped to the
        // ticket's own department here, to prove the rejection is role-based
        // (transfer is CS-Manager-only now), not a department-scoping gap.
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(caller, 2, true, DateTime.UtcNow, null));

        var result = await f.Service.TransferAsync(
            caller, [role], ticket.TicketId, new TransferTicketRequestDto(targetDepartment.DepartmentId, "Misrouted", []));

        Assert.Equal(expectSuccess ? TicketMutationOutcome.Success : TicketMutationOutcome.Forbidden, result.Outcome);
        if (expectSuccess)
        {
            Assert.Equal(targetDepartment.DepartmentId, ticket.CurrentDepartmentId);
            Assert.Null(ticket.CurrentOwnerEmployeeId);
        }
        else
        {
            Assert.Equal(2, ticket.CurrentDepartmentId);
            Assert.Equal(0, f.UnitOfWork.SaveChangesCallCount);
        }
    }

    [Fact]
    public async Task TransferAsync_ByCsManager_MovesDepartmentAndClearsOwner()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        ticket.AssignTo(Guid.NewGuid());
        var targetDepartment = f.Departments.AddDepartment("Facility Management", "FM");

        var result = await f.Service.TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(targetDepartment.DepartmentId, "Misrouted", []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(targetDepartment.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Contains(f.Audit.Written, w => w.Action == "Transfer");
    }

    [Fact]
    public async Task TransferAsync_ToInactiveDepartment_ReturnsTargetDepartmentInactive()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        var inactiveDepartment = f.Departments.AddDepartment("Legacy", "LG", isActive: false);

        var result = await f.Service.TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(inactiveDepartment.DepartmentId, "Misrouted", []));

        Assert.Equal(TicketMutationOutcome.TargetDepartmentInactive, result.Outcome);
    }

    [Fact]
    public async Task TransferAsync_ToSameDepartment_ReturnsAlreadyInTargetDepartment()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);

        var result = await f.Service.TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(2, "No-op", []));

        Assert.Equal(TicketMutationOutcome.AlreadyInTargetDepartment, result.Outcome);
    }

    [Fact]
    public async Task TransferAsync_OnClosedTicket_ReturnsTicketClosed_NoDatabaseWrites()
    {
        var f = CreateService();
        var ticket = await SeedClosedTicketAsync(f.Tickets, departmentId: 2);
        var targetDepartment = f.Departments.AddDepartment("Facility Management", "FM");

        var result = await f.Service.TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(targetDepartment.DepartmentId, "Misrouted", []));

        Assert.Equal(TicketMutationOutcome.TicketClosed, result.Outcome);
        Assert.Equal(2, ticket.CurrentDepartmentId);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, f.UnitOfWork.SaveChangesCallCount);
        Assert.DoesNotContain(f.Audit.Written, w => w.Action == "Transfer");
    }
}

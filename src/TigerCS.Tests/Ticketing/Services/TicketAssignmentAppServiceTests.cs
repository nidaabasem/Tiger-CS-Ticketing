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

    [Fact]
    public async Task AssignAsync_SelfClaimByDepartmentMember_Succeeds()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var employeeId = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employeeId, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));

        var result = await f.Service.AssignAsync(
            employeeId, [Roles.DepartmentEmployee], ticket.TicketId,
            new AssignTicketRequestDto(employeeId, []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(employeeId, ticket.CurrentOwnerEmployeeId);
        Assert.Single(f.Assignments.Added);
        Assert.Contains(f.Audit.Written, w => w.Action == "Assign");
        Assert.Equal(1, f.UnitOfWork.TransactionsCommitted);
    }

    [Fact]
    public async Task AssignAsync_SelfClaimByNonMemberOfDepartment_ReturnsForbidden_PreventsUnauthorizedSelfAssignment()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var employeeId = Guid.NewGuid();
        // Not added to any department assignment, and holds no cross-department role.

        var result = await f.Service.AssignAsync(
            employeeId, [Roles.DepartmentEmployee], ticket.TicketId,
            new AssignTicketRequestDto(employeeId, []));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task AssignAsync_AssignedEmployeeNotInTicketDepartment_ReturnsEmployeeNotInDepartment()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var caller = Guid.NewGuid();
        var target = Guid.NewGuid();
        // Caller is Supervisor (cross-department), but target employee is a member of a DIFFERENT department.
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(target, 999, true, DateTime.UtcNow, null));

        var result = await f.Service.AssignAsync(
            caller, [Roles.CsSupervisor], ticket.TicketId,
            new AssignTicketRequestDto(target, []));

        Assert.Equal(TicketMutationOutcome.EmployeeNotInDepartment, result.Outcome);
    }

    [Fact]
    public async Task AssignAsync_ReassignByPlainDepartmentEmployeeToSomeoneElse_ReturnsForbidden()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var caller = Guid.NewGuid();
        var target = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(caller, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(target, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));

        var result = await f.Service.AssignAsync(
            caller, [Roles.DepartmentEmployee], ticket.TicketId,
            new AssignTicketRequestDto(target, []));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task AssignAsync_ByDepartmentHeadOfDifferentDepartment_ReturnsForbidden_PreventsCrossDepartmentAssignment()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        var caller = Guid.NewGuid();
        var target = Guid.NewGuid();
        // Department Head, but for department 999, not the ticket's department 2.
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(caller, 999, true, DateTime.UtcNow, null));
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(target, 2, true, DateTime.UtcNow, null));

        var result = await f.Service.AssignAsync(
            caller, [Roles.DepartmentHead], ticket.TicketId,
            new AssignTicketRequestDto(target, []));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task AssignAsync_ConcurrentModification_ReturnsConcurrencyConflictAndRollsBack()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var employeeId = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employeeId, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));
        f.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = 1;

        var result = await f.Service.AssignAsync(
            employeeId, [Roles.DepartmentEmployee], ticket.TicketId,
            new AssignTicketRequestDto(employeeId, []));

        Assert.Equal(TicketMutationOutcome.ConcurrencyConflict, result.Outcome);
        Assert.Equal(1, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, f.UnitOfWork.TransactionsCommitted);
        Assert.Equal(1, f.UnitOfWork.TransactionsRolledBack);
    }

    [Fact]
    public async Task TransferAsync_BySupervisor_MovesDepartmentAndClearsOwner()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        ticket.AssignTo(Guid.NewGuid());
        var targetDepartment = f.Departments.AddDepartment("Facility Management", "FM");
        var caller = Guid.NewGuid();

        var result = await f.Service.TransferAsync(
            caller, [Roles.CsSupervisor], ticket.TicketId,
            new TransferTicketRequestDto(targetDepartment.DepartmentId, "Misrouted", []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(targetDepartment.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Contains(f.Audit.Written, w => w.Action == "Transfer");
    }

    [Fact]
    public async Task TransferAsync_ByDepartmentEmployee_ReturnsForbidden()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        var targetDepartment = f.Departments.AddDepartment("Facility Management", "FM");
        var caller = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(caller, 2, true, DateTime.UtcNow, null));

        var result = await f.Service.TransferAsync(
            caller, [Roles.DepartmentEmployee], ticket.TicketId,
            new TransferTicketRequestDto(targetDepartment.DepartmentId, "Misrouted", []));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
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
}

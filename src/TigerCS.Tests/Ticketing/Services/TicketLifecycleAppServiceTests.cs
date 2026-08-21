using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

public class TicketLifecycleAppServiceTests
{
    private sealed record Fixture(
        TicketLifecycleAppService Service,
        FakeTicketRepository Tickets,
        FakeTicketResolutionRepository Resolutions,
        FakeTicketStatusHistoryRepository StatusHistory,
        FakeUserDepartmentAssignmentRepository DepartmentAssignments,
        FakeAuditEntryWriter Audit,
        FakeTicketingUnitOfWork UnitOfWork,
        SlaServiceFixture Sla);

    private static Fixture CreateService()
    {
        var tickets = new FakeTicketRepository();
        var resolutions = new FakeTicketResolutionRepository();
        var statusHistory = new FakeTicketStatusHistoryRepository();
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository();
        var audit = new FakeAuditEntryWriter();
        var unitOfWork = new FakeTicketingUnitOfWork();

        // Resolving is the Resolution SLA's achievement event, so this
        // service now finalizes the breach flags through the same processor
        // the background jobs use.
        var sla = new SlaServiceFixture(tickets, resolutions, statusHistory, departmentAssignments, audit, unitOfWork);

        var service = new TicketLifecycleAppService(
            tickets, resolutions, statusHistory, departmentAssignments, unitOfWork, audit, sla.BreachProcessor, TimeProvider.System);

        return new Fixture(service, tickets, resolutions, statusHistory, departmentAssignments, audit, unitOfWork, sla);
    }

    private static async Task<Ticket> SeedInProgressTicketAsync(FakeTicketRepository repo, Guid ownerEmployeeId, int departmentId = 2)
    {
        var ticket = Ticket.CreateVerified(
            "TG-CS-20260821-0010", departmentId, unitReferenceId: 10, contactReferenceId: 20,
            categoryId: 5, priorityId: (byte)PriorityLevel.High, "AC not cooling", DateTime.UtcNow);
        await repo.AddAsync(ticket);
        ticket.AssignTo(ownerEmployeeId);
        ticket.ChangeStatus(TicketStatus.InProgress);
        return ticket;
    }

    [Fact]
    public async Task ChangeStatusAsync_ByCurrentOwner_Succeeds()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);

        var result = await f.Service.ChangeStatusAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ChangeStatusRequestDto("PendingCustomer", []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(TicketStatus.PendingCustomer, ticket.TicketStatus);
        Assert.Single(f.StatusHistory.Added);
        Assert.Equal(1, f.UnitOfWork.TransactionsCommitted);
    }

    [Fact]
    public async Task ChangeStatusAsync_ByUnrelatedEmployee_ReturnsForbidden()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);

        var result = await f.Service.ChangeStatusAsync(
            stranger, [Roles.DepartmentEmployee], ticket.TicketId,
            new ChangeStatusRequestDto("PendingCustomer", []));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
    }

    [Fact]
    public async Task ChangeStatusAsync_InvalidTransition_ReturnsDeterministicOutcome()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);

        var result = await f.Service.ChangeStatusAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ChangeStatusRequestDto("Closed", []));

        Assert.Equal(TicketMutationOutcome.InvalidStatusTransition, result.Outcome);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
        Assert.Empty(f.StatusHistory.Added);
    }

    [Fact]
    public async Task ResolveAsync_ByDepartmentEmployeeOwner_Succeeds_WritesResolutionHistoryAndAudit()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);

        var result = await f.Service.ResolveAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Fixed the AC unit.", null, null, []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(TicketStatus.Resolved, ticket.TicketStatus);
        Assert.Single(f.Resolutions.Added);
        Assert.Equal("Fixed the AC unit.", f.Resolutions.Added[0].ResolutionNote);
        // TicketStatus + ResolutionOutcome dimensions, both in the same correlated write.
        Assert.Equal(2, f.StatusHistory.Added.Count);
        Assert.Contains(f.Audit.Written, w => w.Action == "Resolve");
        Assert.Equal(1, f.UnitOfWork.TransactionsCommitted);
    }

    [Fact]
    public async Task ResolveAsync_ByDepartmentEmployeeNotTheOwner_ReturnsForbidden()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var colleague = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);

        var result = await f.Service.ResolveAsync(
            colleague, [Roles.DepartmentEmployee], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Fixed it.", null, null, []));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task ResolveAsync_ByDepartmentHeadOfSameDepartment_Succeeds_WithoutBeingOwner()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var deptHead = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner, departmentId: 2);
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(deptHead, 2, true, DateTime.UtcNow, null));

        var result = await f.Service.ResolveAsync(
            deptHead, [Roles.DepartmentHead], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Verified and closed out the work order.", null, null, []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task ResolveAsync_ByCsAgent_ReturnsForbidden_Issue022ResolveIsDepartmentOnly()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);

        var result = await f.Service.ResolveAsync(
            owner, [Roles.CsAgent], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Fixed it.", null, null, []));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task ResolveAsync_FromOpenTicket_ReturnsNotEligibleForResolution()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = Ticket.CreateVerified(
            "TG-CS-20260821-0011", 2, 10, 20, 5, (byte)PriorityLevel.High, "AC not cooling", DateTime.UtcNow);
        await f.Tickets.AddAsync(ticket);
        ticket.AssignTo(owner);
        // Still Open — never moved to InProgress.

        var result = await f.Service.ResolveAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Fixed it.", null, null, []));

        Assert.Equal(TicketMutationOutcome.NotEligibleForResolution, result.Outcome);
    }

    [Fact]
    public async Task ResolveAsync_DuplicateOfAnotherDuplicate_ReturnsDuplicateChainNotAllowed()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var originalDuplicate = await SeedInProgressTicketAsync(f.Tickets, owner, departmentId: 2);
        originalDuplicate.Resolve(ResolutionOutcome.Duplicate, duplicateOfTicketId: 12345);

        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner, departmentId: 2);

        var result = await f.Service.ResolveAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ResolveTicketRequestDto("Duplicate", "Same as another ticket.", null, originalDuplicate.TicketId, []));

        Assert.Equal(TicketMutationOutcome.DuplicateChainNotAllowed, result.Outcome);
    }

    [Fact]
    public async Task CloseAsync_ByCsAgentAfterResolve_Succeeds()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);
        await f.Service.ResolveAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Fixed it.", null, null, []));

        var closer = Guid.NewGuid();
        var result = await f.Service.CloseAsync(closer, [Roles.CsAgent], ticket.TicketId, new CloseTicketRequestDto([]));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Contains(f.Audit.Written, w => w.Action == "Close");
    }

    [Fact]
    public async Task CloseAsync_ByDepartmentEmployee_ReturnsForbidden_Issue022CloseIsCsLayerOnly()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);
        await f.Service.ResolveAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Fixed it.", null, null, []));

        var result = await f.Service.CloseAsync(owner, [Roles.DepartmentEmployee], ticket.TicketId, new CloseTicketRequestDto([]));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
        Assert.Equal(TicketStatus.Resolved, ticket.TicketStatus);
    }

    [Fact]
    public async Task CloseAsync_WithoutPriorResolve_ReturnsNotYetResolved()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);

        var result = await f.Service.CloseAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId, new CloseTicketRequestDto([]));

        Assert.Equal(TicketMutationOutcome.NotYetResolved, result.Outcome);
    }

    [Fact]
    public async Task ResolveAsync_ConcurrentModification_ReturnsConcurrencyConflictAndRollsBackTransaction()
    {
        var f = CreateService();
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(f.Tickets, owner);
        f.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = 1;

        var result = await f.Service.ResolveAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Fixed it.", null, null, []));

        Assert.Equal(TicketMutationOutcome.ConcurrencyConflict, result.Outcome);
        Assert.Equal(1, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, f.UnitOfWork.TransactionsCommitted);
        Assert.Equal(1, f.UnitOfWork.TransactionsRolledBack);
    }

    private static async Task<(Ticket Ticket, Guid Owner)> SeedClosedTicketAsync(FakeTicketRepository repo, int departmentId = 2)
    {
        var owner = Guid.NewGuid();
        var ticket = await SeedInProgressTicketAsync(repo, owner, departmentId);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        ticket.Close();
        return (ticket, owner);
    }

    // ---- Closed-ticket immutability (PR correction) — every mutating operation, no database writes ----

    [Fact]
    public async Task ChangeStatusAsync_OnClosedTicket_ReturnsTicketClosed_NoDatabaseWrites()
    {
        var f = CreateService();
        var (ticket, owner) = await SeedClosedTicketAsync(f.Tickets);

        var result = await f.Service.ChangeStatusAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ChangeStatusRequestDto("InProgress", []));

        Assert.Equal(TicketMutationOutcome.TicketClosed, result.Outcome);
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, f.UnitOfWork.SaveChangesCallCount);
        Assert.Empty(f.StatusHistory.Added);
    }

    [Fact]
    public async Task ResolveAsync_OnClosedTicket_ReturnsTicketClosed_NoDatabaseWrites()
    {
        var f = CreateService();
        var (ticket, owner) = await SeedClosedTicketAsync(f.Tickets);
        var resolutionsBefore = f.Resolutions.Added.Count;

        var result = await f.Service.ResolveAsync(
            owner, [Roles.DepartmentEmployee], ticket.TicketId,
            new ResolveTicketRequestDto("Resolved", "Attempted re-resolution.", null, null, []));

        Assert.Equal(TicketMutationOutcome.TicketClosed, result.Outcome);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(resolutionsBefore, f.Resolutions.Added.Count);
    }

    [Fact]
    public async Task CloseAsync_OnAlreadyClosedTicket_ReturnsTicketClosed_NoDatabaseWrites()
    {
        var f = CreateService();
        var (ticket, _) = await SeedClosedTicketAsync(f.Tickets);

        var result = await f.Service.CloseAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId, new CloseTicketRequestDto([]));

        Assert.Equal(TicketMutationOutcome.TicketClosed, result.Outcome);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, f.UnitOfWork.SaveChangesCallCount);
    }
}

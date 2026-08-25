using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

public class TicketReconciliationAppServiceTests
{
    private sealed record Fixture(
        TicketReconciliationAppService Service,
        FakeTicketRepository Tickets,
        FakeIntakeRecordRepository IntakeRecords,
        FakeVerificationSessionRepository Sessions,
        FakeTicketRequesterSnapshotRepository Snapshots,
        FakeTicketStatusHistoryRepository StatusHistory,
        FakeAuditEntryWriter Audit,
        FakeTicketingUnitOfWork UnitOfWork);

    private static Fixture CreateService()
    {
        var tickets = new FakeTicketRepository();
        var intakeRecords = new FakeIntakeRecordRepository();
        var sessions = new FakeVerificationSessionRepository();
        var snapshots = new FakeTicketRequesterSnapshotRepository();
        var statusHistory = new FakeTicketStatusHistoryRepository();
        var audit = new FakeAuditEntryWriter();
        var unitOfWork = new FakeTicketingUnitOfWork();

        // Business-rule change: every ticket's SLA clock now starts at
        // creation, so reconciliation no longer opens an SLA period — no
        // SlaDueDateService dependency here any more.
        var service = new TicketReconciliationAppService(
            tickets, intakeRecords, sessions, snapshots, statusHistory, unitOfWork, audit, TimeProvider.System);

        return new Fixture(service, tickets, intakeRecords, sessions, snapshots, statusHistory, audit, unitOfWork);
    }

    private static async Task<(Ticket Ticket, IntakeRecord Intake, Guid AgentId)> SeedUnverifiedTicketAsync(
        Fixture f, string rawUnitNumberEntered = "1204")
    {
        var agentId = Guid.NewGuid();
        var ticket = Ticket.CreateUnverified(
            "TG-CS-20260821-0020", departmentId: 2, categoryId: 5,
            priorityId: (byte)PriorityLevel.Critical, "Flooding reported", DateTime.UtcNow);
        await f.Tickets.AddAsync(ticket);

        var intake = new IntakeRecord(Channel.Phone, "+971500000001", null, isUnitRelated: true, rawUnitNumberEntered, priorityHint: null, agentId, DateTime.UtcNow);
        await f.IntakeRecords.AddAsync(intake);
        intake.LinkToTicket(ticket.TicketId, CrmVerificationStatus.Unverified);

        return (ticket, intake, agentId);
    }

    private static VerificationSession ConfirmedSession(Guid agentId, string snapshotUnitNumber, int unitReferenceId = 30, int contactReferenceId = 40)
    {
        var session = new VerificationSession(
            Guid.NewGuid(), agentId, unitReferenceId, contactReferenceId,
            snapshotUnitNumber, "Tiger Tower A", "Tower A", "Residential", "Ahmed Al-Farsi", "email",
            DateTime.UtcNow, DateTime.UtcNow.AddMinutes(30), idempotencyKey: null);
        session.Confirm(DateTime.UtcNow, VerificationMethod.ManualAgentConfirmation);
        return session;
    }

    [Fact]
    public async Task ReconcileAsync_MatchingUnitContext_PopulatesReferencesAndCreatesSnapshotExactlyOnce()
    {
        var f = CreateService();
        var (ticket, _, agentId) = await SeedUnverifiedTicketAsync(f, "1204");
        var session = ConfirmedSession(agentId, "1204");
        await f.Sessions.AddAsync(session);

        var result = await f.Service.ReconcileAsync(
            agentId, ticket.TicketId, new ReconcileTicketRequestDto(session.VerificationSessionId, []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(CrmVerificationStatus.Verified, ticket.VerificationStatus);
        Assert.Equal(30, ticket.UnitReferenceId);
        Assert.Equal(40, ticket.ContactReferenceId);
        Assert.Single(f.Snapshots.Added);
        Assert.Equal(VerificationSessionStatus.Consumed, session.Status);
        Assert.Contains(f.Audit.Written, w => w.Action == "Reconcile");
        Assert.Contains(f.Audit.Written, w => w.Action == "ConsumeVerificationSession");
        Assert.Equal(1, f.UnitOfWork.TransactionsCommitted);
    }

    [Fact]
    public async Task ReconcileAsync_MismatchedUnitContext_ReturnsReconciliationUnitMismatch_DoesNotAttachWrongUnit()
    {
        var f = CreateService();
        var (ticket, _, agentId) = await SeedUnverifiedTicketAsync(f, "1204");
        // Agent confirmed a DIFFERENT unit than the one originally reported for this ticket.
        var session = ConfirmedSession(agentId, "9999");
        await f.Sessions.AddAsync(session);

        var result = await f.Service.ReconcileAsync(
            agentId, ticket.TicketId, new ReconcileTicketRequestDto(session.VerificationSessionId, []));

        Assert.Equal(TicketMutationOutcome.ReconciliationUnitMismatch, result.Outcome);
        Assert.Equal(CrmVerificationStatus.Unverified, ticket.VerificationStatus);
        Assert.Null(ticket.UnitReferenceId);
        Assert.Empty(f.Snapshots.Added);
        Assert.Equal(VerificationSessionStatus.Confirmed, session.Status);
    }

    [Fact]
    public async Task ReconcileAsync_AlreadyVerifiedTicket_ReturnsAlreadyVerified()
    {
        var f = CreateService();
        var agentId = Guid.NewGuid();
        var ticket = Ticket.CreateVerified(
            "TG-CS-20260821-0021", 2, 10, 20, 5, (byte)PriorityLevel.High, "AC not cooling", DateTime.UtcNow);
        await f.Tickets.AddAsync(ticket);
        var session = ConfirmedSession(agentId, "1204");
        await f.Sessions.AddAsync(session);

        var result = await f.Service.ReconcileAsync(
            agentId, ticket.TicketId, new ReconcileTicketRequestDto(session.VerificationSessionId, []));

        Assert.Equal(TicketMutationOutcome.AlreadyVerified, result.Outcome);
    }

    [Fact]
    public async Task ReconcileAsync_SessionOwnedByDifferentAgent_ReturnsForbidden()
    {
        var f = CreateService();
        var (ticket, _, agentId) = await SeedUnverifiedTicketAsync(f, "1204");
        var session = ConfirmedSession(Guid.NewGuid(), "1204"); // different owner
        await f.Sessions.AddAsync(session);

        var result = await f.Service.ReconcileAsync(
            agentId, ticket.TicketId, new ReconcileTicketRequestDto(session.VerificationSessionId, []));

        Assert.Equal(TicketMutationOutcome.VerificationSessionForbidden, result.Outcome);
    }

    [Fact]
    public async Task ReconcileAsync_SessionAlreadyConsumed_ReturnsAlreadyConsumed_SafeToRetryNeverDoubleReconciles()
    {
        var f = CreateService();
        var (ticket, _, agentId) = await SeedUnverifiedTicketAsync(f, "1204");
        var session = ConfirmedSession(agentId, "1204");
        session.Consume(999, DateTime.UtcNow);
        await f.Sessions.AddAsync(session);

        var result = await f.Service.ReconcileAsync(
            agentId, ticket.TicketId, new ReconcileTicketRequestDto(session.VerificationSessionId, []));

        Assert.Equal(TicketMutationOutcome.VerificationSessionAlreadyConsumed, result.Outcome);
        Assert.Equal(CrmVerificationStatus.Unverified, ticket.VerificationStatus);
    }

    [Fact]
    public async Task ReconcileAsync_ConcurrentSessionConsumption_ReturnsAlreadyConsumedAndRollsBackTicketChange()
    {
        var f = CreateService();
        var (ticket, _, agentId) = await SeedUnverifiedTicketAsync(f, "1204");
        var session = ConfirmedSession(agentId, "1204");
        await f.Sessions.AddAsync(session);
        f.UnitOfWork.ThrowConcurrencyConflictOnCall = 1;

        var result = await f.Service.ReconcileAsync(
            agentId, ticket.TicketId, new ReconcileTicketRequestDto(session.VerificationSessionId, []));

        Assert.Equal(TicketMutationOutcome.VerificationSessionAlreadyConsumed, result.Outcome);
        Assert.Equal(1, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, f.UnitOfWork.TransactionsCommitted);
        Assert.Equal(1, f.UnitOfWork.TransactionsRolledBack);
    }

    [Fact]
    public async Task ReconcileAsync_TicketConcurrentlyModified_ReturnsConcurrencyConflict()
    {
        var f = CreateService();
        var (ticket, _, agentId) = await SeedUnverifiedTicketAsync(f, "1204");
        var session = ConfirmedSession(agentId, "1204");
        await f.Sessions.AddAsync(session);
        f.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = 1;

        var result = await f.Service.ReconcileAsync(
            agentId, ticket.TicketId, new ReconcileTicketRequestDto(session.VerificationSessionId, []));

        Assert.Equal(TicketMutationOutcome.ConcurrencyConflict, result.Outcome);
    }
}

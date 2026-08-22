using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Notifications.Services;

/// <summary>
/// ADR-0013 / NFR-REL-01's central guarantee, tested at the boundary where it
/// actually has to hold: the ticket and its Outbox message commit together,
/// or neither does.
/// </summary>
public class TicketCreationOutboxAtomicityTests
{
    private sealed record Fixture(
        TicketCreationAppService Service,
        FakeIntakeRecordRepository IntakeRecords,
        FakeVerificationSessionRepository Sessions,
        FakeCategoryRepository Categories,
        FakeDepartmentRepository Departments,
        FakeTicketRepository Tickets,
        FakeAuditEntryWriter Audit,
        FakeTicketingUnitOfWork UnitOfWork,
        FakeOutboxWriter Outbox);

    private static Fixture CreateService()
    {
        var intakeRecords = new FakeIntakeRecordRepository();
        var sessions = new FakeVerificationSessionRepository();
        var categories = new FakeCategoryRepository();
        var priorities = new FakePriorityRepository();
        var departments = new FakeDepartmentRepository();
        var tickets = new FakeTicketRepository();
        var snapshots = new FakeTicketRequesterSnapshotRepository();
        var statusHistory = new FakeTicketStatusHistoryRepository();
        var audit = new FakeAuditEntryWriter();
        var unitOfWork = new FakeTicketingUnitOfWork();
        var outbox = new FakeOutboxWriter();
        unitOfWork.OutboxWriter = outbox;

        var sla = new SlaServiceFixture(tickets, statusHistory: statusHistory, audit: audit, unitOfWork: unitOfWork);

        var service = new TicketCreationAppService(
            intakeRecords, sessions, categories, priorities, departments,
            tickets, snapshots, statusHistory, unitOfWork, audit, outbox, sla.DueDates, TimeProvider.System);

        return new Fixture(service, intakeRecords, sessions, categories, departments, tickets, audit, unitOfWork, outbox);
    }

    private static async Task<(IntakeRecord Record, Guid AgentId)> SeedUnitRelatedIntakeAsync(FakeIntakeRecordRepository repo)
    {
        var agentId = Guid.NewGuid();
        var record = new IntakeRecord(Channel.Phone, isUnitRelated: true, "1204", priorityHint: null, agentId, DateTime.UtcNow);
        await repo.AddAsync(record);
        return (record, agentId);
    }

    private static VerificationSession ConfirmedSession(Guid agentId)
    {
        var session = new VerificationSession(
            Guid.NewGuid(), agentId, 1, 1, "1204", "Tiger Tower A", "Tower A", "Residential",
            "Ahmed Al-Farsi", "ahmed@example.com", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(30), idempotencyKey: null);
        session.Confirm(DateTime.UtcNow, VerificationMethod.ManualAgentConfirmation);
        return session;
    }

    [Fact]
    public async Task SuccessfulCreation_CommitsTheTicketAndItsOutboxMessageTogether()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);
        var session = ConfirmedSession(agentId);
        await f.Sessions.AddAsync(session);

        var result = await f.Service.CreateFromVerificationSessionAsync(
            agentId, new CreateTicketFromVerificationRequestDto(
                intake.IntakeRecordId, session.VerificationSessionId, category.CategoryId, 2, "AC not cooling."));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        Assert.Equal(1, f.UnitOfWork.TransactionsCommitted);

        var message = Assert.Single(f.Outbox.Committed);
        Assert.Empty(f.Outbox.Staged);
        Assert.Equal(OutboxEventTypes.TicketCreated, message.EventType);
        Assert.Equal(OutboxMessageStatus.Pending, message.Status);

        // The payload carries the ticket's real, database-generated id — the
        // write happens after the ticket insert, inside the same transaction.
        var ticket = Assert.Single(f.Tickets.All);
        var payload = TicketCreatedEventPayload.FromJson(message.Payload);
        Assert.NotNull(payload);
        Assert.Equal(ticket.TicketId, payload!.TicketId);

        // Identifiers only — no customer data is duplicated into an
        // operator-visible infrastructure row.
        Assert.DoesNotContain("AC not cooling", message.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ahmed", message.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ahmed@example.com", message.Payload, StringComparison.OrdinalIgnoreCase);

        // "Notification queued" (ADR-0018), written synchronously in the
        // business transaction.
        var queued = Assert.Single(f.Audit.Entries, e => e.Action == NotificationAuditActions.NotificationQueued);
        Assert.Equal(message.CorrelationId, queued.CorrelationId);
        Assert.Equal(message.OutboxMessageId.ToString(), queued.EntityId);
    }

    /// <summary>
    /// The other half of the guarantee, and the more important one: a business
    /// transaction that rolls back leaves no Outbox message, so a ticket that
    /// does not exist can never acknowledge anything.
    /// </summary>
    [Fact]
    public async Task RolledBackCreation_LeavesNoOutboxMessage()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);
        var session = ConfirmedSession(agentId);
        await f.Sessions.AddAsync(session);

        // A concurrent request consumed the same session first: the second
        // SaveChanges loses the optimistic-concurrency race and the whole
        // transaction rolls back.
        f.UnitOfWork.ThrowConcurrencyConflictOnCall = 2;

        var result = await f.Service.CreateFromVerificationSessionAsync(
            agentId, new CreateTicketFromVerificationRequestDto(
                intake.IntakeRecordId, session.VerificationSessionId, category.CategoryId, 2, "AC not cooling."));

        Assert.Equal(TicketCreationOutcome.VerificationSessionAlreadyConsumed, result.Outcome);
        Assert.Equal(0, f.UnitOfWork.TransactionsCommitted);
        Assert.Equal(1, f.UnitOfWork.TransactionsRolledBack);

        Assert.Empty(f.Outbox.Committed);
        Assert.Empty(f.Outbox.Staged);
    }

    /// <summary>A ticket-number collision aborts before the Outbox write is even reached — nothing staged, nothing committed.</summary>
    [Fact]
    public async Task TicketNumberCollision_LeavesNoOutboxMessage()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Customer Service", "CS");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);
        var session = ConfirmedSession(agentId);
        await f.Sessions.AddAsync(session);

        f.UnitOfWork.ThrowDuplicateWriteExceptionOnCall = 1;

        var result = await f.Service.CreateFromVerificationSessionAsync(
            agentId, new CreateTicketFromVerificationRequestDto(
                intake.IntakeRecordId, session.VerificationSessionId, category.CategoryId, 2, "AC not cooling."));

        Assert.Equal(TicketCreationOutcome.TicketNumberCollision, result.Outcome);
        Assert.Empty(f.Outbox.Committed);
        Assert.Empty(f.Outbox.Staged);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == NotificationAuditActions.NotificationQueued);
    }

    /// <summary>
    /// FR-NOT-01: "email attempted for every ticket". A provisional
    /// (ISSUE-006) ticket has no verified requester to write to, but the event
    /// is still enqueued so the failure is visible rather than the ticket
    /// being silently skipped.
    /// </summary>
    [Fact]
    public async Task ProvisionalTicket_AlsoEnqueuesTheAcknowledgementEvent()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Facilities", "FM");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);

        var result = await f.Service.CreateProvisionalAsync(
            agentId, new CreateProvisionalTicketRequestDto(
                intake.IntakeRecordId, category.CategoryId, PriorityId: 1, "Lift stuck between floors."));

        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        var message = Assert.Single(f.Outbox.Committed);
        Assert.Equal(OutboxEventTypes.TicketCreated, message.EventType);
        Assert.Contains(f.Audit.Entries, e => e.Action == NotificationAuditActions.NotificationQueued);
    }

    /// <summary>
    /// A Medium/Low request during a CRM outage stays queued on its
    /// IntakeRecord and never becomes a ticket (ISSUE-006), so there is
    /// nothing to acknowledge and no event may be written.
    /// </summary>
    [Fact]
    public async Task QueuedPendingVerification_EnqueuesNothing()
    {
        var f = CreateService();
        var department = f.Departments.AddDepartment("Facilities", "FM");
        var category = f.Categories.Seed(department.DepartmentId);
        var (intake, agentId) = await SeedUnitRelatedIntakeAsync(f.IntakeRecords);

        var result = await f.Service.CreateProvisionalAsync(
            agentId, new CreateProvisionalTicketRequestDto(
                intake.IntakeRecordId, category.CategoryId, PriorityId: 3, "Tap dripping."));

        Assert.Equal(TicketCreationOutcome.QueuedPendingVerification, result.Outcome);
        Assert.Empty(f.Outbox.Committed);
        Assert.Empty(f.Outbox.Staged);
    }

    /// <summary>
    /// ADR-0014's enqueue-side dedup: one logical event produces at most one
    /// Outbox message, so two tickets get two messages and a repeated key gets
    /// none.
    /// </summary>
    [Fact]
    public async Task DuplicateIdempotencyKey_ProducesNoSecondMessage()
    {
        var f = CreateService();
        var key = OutboxEventTypes.IdempotencyKeyFor(OutboxEventTypes.TicketCreated, 1, 1);

        var first = await f.Outbox.WriteAsync(OutboxEventTypes.TicketCreated, "{}", Guid.NewGuid(), key, DateTime.UtcNow);
        var second = await f.Outbox.WriteAsync(OutboxEventTypes.TicketCreated, "{}", Guid.NewGuid(), key, DateTime.UtcNow);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Single(f.Outbox.Staged);
    }
}

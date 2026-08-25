using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Dto;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Notifications.Fakes;

/// <summary>
/// Builds the Outbox dispatcher and the acknowledgement handler over
/// in-memory fakes, wired exactly as the composition root wires them, so a
/// test exercises the real reliability logic rather than a re-implementation
/// of it.
/// </summary>
public sealed class NotificationServiceFixture
{
    public FakeOutboxMessageRepository OutboxMessages { get; } = new();
    public FakeNotificationRepository Notifications { get; } = new();
    public FakeNotificationsUnitOfWork UnitOfWork { get; } = new();
    public FakeEmailSender Email { get; } = new();
    public FakeTicketRepository Tickets { get; } = new();
    public FakeTicketRequesterSnapshotRepository Snapshots { get; } = new();
    public FakeDepartmentRepository Departments { get; } = new();
    public FakeAuditEntryWriter Audit { get; } = new();
    public FakeTicketSlaInstanceRepository SlaInstances { get; } = new();

    public OutboxDispatchPolicy Policy { get; set; } = OutboxDispatchPolicy.Default;

    /// <summary>Mutable so a test can advance past a backoff window without waiting for one.</summary>
    public FakeTimeProvider Time { get; } = new(new DateTime(2026, 8, 22, 9, 0, 0, DateTimeKind.Utc));

    public TicketAcknowledgementHandler CreateHandler() => new(
        Tickets, Snapshots, SlaInstances, Departments, Notifications, Email, Audit, Time);

    public OutboxDispatcher CreateDispatcher(params Application.Modules.Notifications.Abstractions.IOutboxEventHandler[] handlers)
    {
        // So a failed save reverts the in-memory message exactly as EF's
        // ReloadAsync would — see FakeNotificationsUnitOfWork's remarks.
        UnitOfWork.Track(OutboxMessages.Messages);

        return new OutboxDispatcher(
            OutboxMessages,
            handlers.Length > 0 ? handlers : [CreateHandler()],
            UnitOfWork,
            Audit,
            Policy,
            Time,
            NullLogger<OutboxDispatcher>.Instance);
    }

    /// <summary>Seeds a verified ticket with a requester snapshot whose contact channel is a real email address.</summary>
    public async Task<Ticket> SeedVerifiedTicketAsync(string contactChannel = "requester@example.com", string ticketNumber = "TG-CS-20260822-0001")
    {
        var department = Departments.AddDepartment("Customer Service", "CS");

        var ticket = Ticket.CreateVerified(
            ticketNumber, department.DepartmentId, unitReferenceId: 1, contactReferenceId: 1,
            categoryId: 1, priorityId: 2, "AC unit not cooling.", Time.GetUtcNow().UtcDateTime);
        await Tickets.AddAsync(ticket);

        await Snapshots.AddAsync(new TicketRequesterSnapshot(
            ticket.TicketId, "1204", "Tiger Tower A", "Tower A", "Residential",
            "Ahmed Al-Farsi", contactChannel, Time.GetUtcNow().UtcDateTime));

        return ticket;
    }

    /// <summary>An unverified ticket (no customer match at creation) — no requester snapshot exists for it at all.</summary>
    public async Task<Ticket> SeedUnverifiedTicketAsync(string ticketNumber = "TG-CS-20260822-0002")
    {
        var department = Departments.AddDepartment("Facilities", "FM");

        var ticket = Ticket.CreateUnverified(
            ticketNumber, department.DepartmentId, categoryId: 1, priorityId: 1,
            "Lift stuck between floors.", Time.GetUtcNow().UtcDateTime);
        await Tickets.AddAsync(ticket);

        return ticket;
    }

    /// <summary>Enqueues a <c>TicketCreated</c> message exactly as <c>TicketCreationAppService</c> would, and registers it with the dispatcher's repository.</summary>
    public OutboxMessage EnqueueTicketCreated(long ticketId, Guid? correlationId = null, DateTime? occurredAtUtc = null)
    {
        var occurred = occurredAtUtc ?? Time.GetUtcNow().UtcDateTime;
        var record = new IdempotencyRecord(
            OutboxEventTypes.IdempotencyKeyFor(OutboxEventTypes.TicketCreated, ticketId, OutboxEventTypes.TicketCreatedVersion),
            IdempotencyScopes.OutboxDispatch,
            occurred);

        var message = new OutboxMessage(
            Guid.NewGuid(),
            OutboxEventTypes.TicketCreated,
            new TicketCreatedEventPayload(ticketId, OutboxEventTypes.TicketCreatedVersion).ToJson(),
            correlationId ?? Guid.NewGuid(),
            record,
            occurred);

        OutboxMessages.Messages.Add(message);
        return message;
    }
}

/// <summary>Manually advanced clock — a retry-backoff test must be able to move time forward without sleeping.</summary>
public sealed class FakeTimeProvider(DateTime utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = new(utcNow, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan by) => _utcNow = _utcNow.Add(by);
}

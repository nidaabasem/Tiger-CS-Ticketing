using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Abstractions;
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
/// Wires the real notification handlers, the real <see cref="CustomerEmailSender"/>
/// façade and the real <see cref="OutboxDispatcher"/> over in-memory fakes,
/// with the <see cref="FakeEmailSender"/> as the only provider boundary —
/// so no test ever touches SMTP.
/// </summary>
public sealed class NotificationServiceFixture
{
    public FakeOutboxMessageRepository OutboxMessages { get; } = new();
    public FakeNotificationRepository Notifications { get; } = new();
    public FakeNotificationsUnitOfWork UnitOfWork { get; } = new();
    public FakeEmailSender Email { get; } = new();
    public FakeTicketRepository Tickets { get; } = new();
    public FakeTicketRequesterSnapshotRepository Snapshots { get; } = new();
    public FakeTicketInteractionRepository Interactions { get; } = new();
    public FakeTicketResolutionRepository Resolutions { get; } = new();
    public FakeRequestTypeRepository RequestTypes { get; } = new();
    public FakeCategoryRepository Categories { get; } = new();
    public FakeDepartmentRepository Departments { get; } = new();
    public FakeAuditEntryWriter Audit { get; } = new();
    public FakeTicketSlaInstanceRepository SlaInstances { get; } = new();
    public OutboxDispatchPolicy Policy { get; set; } = OutboxDispatchPolicy.Default;
    public CustomerNotificationPolicy NotificationPolicy { get; set; } = CustomerNotificationPolicy.EnabledDefault;
    public FakeTimeProvider Time { get; } = new(new DateTime(2026, 8, 22, 9, 0, 0, DateTimeKind.Utc));

    public CustomerContactResolver CreateContactResolver() => new(Snapshots, Interactions);

    public ICustomerEmailSender CreateCustomerEmailSender() =>
        new CustomerEmailSender(Email, NotificationPolicy, NullLogger<CustomerEmailSender>.Instance);

    public TicketAcknowledgementHandler CreateHandler() => new(
        Tickets, Notifications, CreateContactResolver(), CreateCustomerEmailSender(), Audit, NotificationPolicy, Time,
        RequestTypes, Categories);

    public TicketResolvedNotificationHandler CreateResolvedHandler() => new(
        Tickets, Notifications, CreateContactResolver(), CreateCustomerEmailSender(), Audit, NotificationPolicy, Time,
        Resolutions);

    public TicketClosedNotificationHandler CreateClosedHandler() => new(
        Tickets, Notifications, CreateContactResolver(), CreateCustomerEmailSender(), Audit, NotificationPolicy, Time);

    public TicketReopenedNotificationHandler CreateReopenedHandler() => new(
        Tickets, Notifications, CreateContactResolver(), CreateCustomerEmailSender(), Audit, NotificationPolicy, Time);

    public OutboxDispatcher CreateDispatcher(params IOutboxEventHandler[] handlers)
    {
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

    public async Task<Ticket> SeedUnverifiedTicketAsync(string ticketNumber = "TG-CS-20260822-0002")
    {
        var department = Departments.AddDepartment("Facilities", "FM");
        var ticket = Ticket.CreateUnverified(
            ticketNumber, department.DepartmentId, categoryId: 1, priorityId: 1,
            "Lift stuck between floors.", Time.GetUtcNow().UtcDateTime);
        await Tickets.AddAsync(ticket);
        return ticket;
    }

    /// <summary>A Genesys-sourced ticket: no CRM snapshot, the customer email lives on the originating interaction.</summary>
    public async Task<Ticket> SeedGenesysTicketAsync(string? customerEmail, string? customerName = "Fatima Al-Mansoori", string ticketNumber = "TG-CS-20260822-0003")
    {
        var department = Departments.AddDepartment("Customer Service", "CS");
        var ticket = Ticket.CreateUnclassified(
            ticketNumber, department.DepartmentId, "Enquiry via Genesys.", Time.GetUtcNow().UtcDateTime, null, null);
        await Tickets.AddAsync(ticket);

        await Interactions.AddAsync(TicketInteraction.CreateFromGenesys(
            ticket.TicketId, WellKnownChannels.Phone, "+971500000001", "conv-" + Guid.NewGuid().ToString("N"),
            calledNumber: null, genesysQueueId: null, genesysQueueName: null, genesysAgentId: null, genesysAgentName: null,
            interactionStartedAtUtc: null, direction: "inbound", Time.GetUtcNow().UtcDateTime,
            isOriginatingInteraction: true, customerName: customerName, customerEmail: customerEmail));

        return ticket;
    }

    /// <summary>Moves a seeded ticket to Resolved with a current resolution row, the state the resolved/closed handlers read.</summary>
    public async Task<TicketResolution> ResolveAsync(Ticket ticket, ResolutionOutcome outcome = ResolutionOutcome.Resolved, string note = "Technician replaced the thermostat.")
    {
        if (ticket.CurrentOwnerEmployeeId is null)
        {
            ticket.AssignTo(Guid.NewGuid());
        }

        if (ticket.TicketStatus == TicketStatus.Open)
        {
            ticket.ChangeStatus(TicketStatus.InProgress);
        }

        ticket.Resolve(outcome, outcome == ResolutionOutcome.Duplicate ? 1 : null);
        var resolution = new TicketResolution(
            ticket.TicketId, outcome, note, reasonCode: null,
            outcome == ResolutionOutcome.Duplicate ? 1 : null, Guid.NewGuid(), Time.GetUtcNow().UtcDateTime);
        await Resolutions.AddAsync(resolution);
        return resolution;
    }

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

    public OutboxMessage EnqueueLifecycleEvent(
        string eventType, long ticketId, int reopenCount = 0, Guid? correlationId = null, DateTime? occurredAtUtc = null)
    {
        var occurred = occurredAtUtc ?? Time.GetUtcNow().UtcDateTime;
        var record = new IdempotencyRecord(
            OutboxEventTypes.LifecycleIdempotencyKeyFor(eventType, ticketId, 1, reopenCount),
            IdempotencyScopes.OutboxDispatch,
            occurred);

        var message = new OutboxMessage(
            Guid.NewGuid(),
            eventType,
            new TicketLifecycleEventPayload(ticketId, 1, reopenCount).ToJson(),
            correlationId ?? Guid.NewGuid(),
            record,
            occurred);

        OutboxMessages.Messages.Add(message);
        return message;
    }
}

public sealed class FakeTimeProvider(DateTime utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = new(utcNow, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan by) => _utcNow = _utcNow.Add(by);
}

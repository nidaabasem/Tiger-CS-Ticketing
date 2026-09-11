using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Notifications.Dto;
using TigerCS.Application.Modules.Notifications.Templates;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Notifications.Services;

/// <summary>
/// Customer Email Notifications increment — "your request has been
/// resolved", consumed from the <c>TicketResolved</c> Outbox event that
/// <c>TicketLifecycleAppService.ResolveAsync</c> writes in the resolving
/// transaction.
///
/// <para>
/// The resolution note is quoted only when
/// <see cref="CustomerNotificationPolicy.IncludeResolutionNote"/> allows it;
/// internal ticket notes are never read here at all.
/// </para>
/// </summary>
public sealed class TicketResolvedNotificationHandler(
    ITicketRepository ticketRepository,
    INotificationRepository notificationRepository,
    CustomerContactResolver contactResolver,
    ICustomerEmailSender emailSender,
    IAuditEntryWriter auditWriter,
    CustomerNotificationPolicy policy,
    TimeProvider timeProvider,
    ITicketResolutionRepository resolutionRepository)
    : CustomerTicketEmailHandler(
        ticketRepository, notificationRepository, contactResolver, emailSender, auditWriter, policy, timeProvider)
{
    public override string EventType => OutboxEventTypes.TicketResolved;

    protected override NotificationType NotificationType => NotificationType.Resolved;

    protected override long? ParseTicketId(string payload) => TicketLifecycleEventPayload.FromJson(payload)?.TicketId;

    protected override async Task<CustomerEmailContent> BuildContentAsync(
        Ticket ticket, CustomerContact contact, DateTime occurredAtUtc, CancellationToken cancellationToken)
    {
        // The current resolution when the ticket is still Resolved/Closed;
        // null once it has been reopened (the resolution is archived), in
        // which case the ticket's own outcome byte — also cleared on reopen
        // — leaves the generic "Resolved" wording.
        var resolution = await resolutionRepository.GetCurrentAsync(ticket.TicketId, cancellationToken);
        var outcome = resolution?.ResolutionOutcome
            ?? (ticket.ResolutionOutcome is { } stored ? (ResolutionOutcome?)stored : null);

        return CustomerEmailTemplates.TicketResolved(new CustomerEmailTicketModel(
            ticket.TicketNumber,
            contact.DisplayName,
            resolution?.ResolvedAtUtc ?? occurredAtUtc,
            ResolutionStatus: CustomerEmailTemplates.DescribeResolution(outcome),
            ResolutionMessage: Policy.IncludeResolutionNote ? resolution?.ResolutionNote : null));
    }
}

/// <summary>Customer Email Notifications increment — "your request has been closed", from the <c>TicketClosed</c> Outbox event.</summary>
public sealed class TicketClosedNotificationHandler(
    ITicketRepository ticketRepository,
    INotificationRepository notificationRepository,
    CustomerContactResolver contactResolver,
    ICustomerEmailSender emailSender,
    IAuditEntryWriter auditWriter,
    CustomerNotificationPolicy policy,
    TimeProvider timeProvider)
    : CustomerTicketEmailHandler(
        ticketRepository, notificationRepository, contactResolver, emailSender, auditWriter, policy, timeProvider)
{
    public override string EventType => OutboxEventTypes.TicketClosed;

    protected override NotificationType NotificationType => NotificationType.Closed;

    protected override long? ParseTicketId(string payload) => TicketLifecycleEventPayload.FromJson(payload)?.TicketId;

    protected override Task<CustomerEmailContent> BuildContentAsync(
        Ticket ticket, CustomerContact contact, DateTime occurredAtUtc, CancellationToken cancellationToken) =>
        Task.FromResult(CustomerEmailTemplates.TicketClosed(new CustomerEmailTicketModel(
            ticket.TicketNumber, contact.DisplayName, occurredAtUtc)));
}

/// <summary>Customer Email Notifications increment — "your request has been reopened", from the <c>TicketReopened</c> Outbox event.</summary>
public sealed class TicketReopenedNotificationHandler(
    ITicketRepository ticketRepository,
    INotificationRepository notificationRepository,
    CustomerContactResolver contactResolver,
    ICustomerEmailSender emailSender,
    IAuditEntryWriter auditWriter,
    CustomerNotificationPolicy policy,
    TimeProvider timeProvider)
    : CustomerTicketEmailHandler(
        ticketRepository, notificationRepository, contactResolver, emailSender, auditWriter, policy, timeProvider)
{
    public override string EventType => OutboxEventTypes.TicketReopened;

    protected override NotificationType NotificationType => NotificationType.Reopened;

    protected override long? ParseTicketId(string payload) => TicketLifecycleEventPayload.FromJson(payload)?.TicketId;

    protected override Task<CustomerEmailContent> BuildContentAsync(
        Ticket ticket, CustomerContact contact, DateTime occurredAtUtc, CancellationToken cancellationToken) =>
        Task.FromResult(CustomerEmailTemplates.TicketReopened(new CustomerEmailTicketModel(
            ticket.TicketNumber, contact.DisplayName, occurredAtUtc)));
}

using System.Globalization;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Notifications.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Notifications;

namespace TigerCS.Application.Modules.Notifications.Services;

/// <summary>
/// FR-NOT-01 / backlog S-18 — the automated acknowledgement email, consumed
/// from the <c>TicketCreated</c> Outbox event.
///
/// <para>
/// <b>This path never satisfies the First Response SLA.</b> FR-SLA-05 and
/// ISSUE-019 exist because an acknowledgement fires within seconds of
/// creation and would otherwise "satisfy" every first-response target
/// automatically, making the KPI meaningless as a measure of service. Nothing
/// here calls <c>Ticket.RecordFirstHumanResponse</c>, writes
/// <c>FirstHumanResponseAtUtc</c>, touches <c>SlaState</c>, or reaches
/// <c>SlaBreachProcessor</c> — a successful send writes exactly one ticket
/// field, <c>AcknowledgementSentAtUtc</c>, which is a different column read
/// by different code. The separation is structural, not a convention a future
/// edit could quietly break: this class holds no reference to any service
/// that could record a first response.
/// </para>
///
/// <para>
/// <b>Idempotent, as ADR-0013 requires of every Outbox consumer.</b> Delivery
/// is at-least-once, so this handler can legitimately run twice for one
/// event. Three independent guards stop that becoming two emails:
/// <c>Ticket.AcknowledgementSentAtUtc</c> is write-once and short-circuits
/// before anything is sent; the <c>Notifications</c> row is found rather than
/// recreated, and a <c>Sent</c> one is terminal; and a unique index on
/// <c>(OutboxMessageId, NotificationType)</c> makes a genuine race between two
/// attempts a constraint violation rather than two delivery rows.
/// </para>
///
/// <para>
/// <b>The residual at-least-once window is real and not claimed away.</b> If
/// the process dies after the provider accepts the message but before the
/// commit that records it, a later attempt resends. Closing that would mean
/// recording the send before making it, which turns a duplicate into a
/// <i>lost</i> acknowledgement — the worse failure for a customer-facing
/// receipt. The window is one local database write wide and is stated here
/// rather than hidden.
/// </para>
///
/// <para>
/// <b>Adds to the caller's unit of work; never saves and never sends inside a
/// transaction.</b> The dispatcher commits once per message, after the
/// provider call has returned — holding a database transaction open across a
/// network call would pin locks for the provider's latency.
/// </para>
/// </summary>
public sealed class TicketAcknowledgementHandler(
    ITicketRepository ticketRepository,
    ITicketRequesterSnapshotRepository snapshotRepository,
    ITicketSlaInstanceRepository slaInstanceRepository,
    IDepartmentRepository departmentRepository,
    INotificationRepository notificationRepository,
    IEmailSender emailSender,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider) : IOutboxEventHandler
{
    public string EventType => OutboxEventTypes.TicketCreated;

    public async Task<OutboxHandlingResult> HandleAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var payload = TicketCreatedEventPayload.FromJson(message.Payload);
        if (payload is null || payload.TicketId <= 0)
        {
            // No retry will make malformed JSON parse.
            return OutboxHandlingResult.Permanent("TicketCreated payload could not be parsed.");
        }

        var ticket = await ticketRepository.GetByIdAsync(payload.TicketId, cancellationToken);
        if (ticket is null)
        {
            // Nothing in this system deletes a ticket (MVP-ERD.md's
            // Restrict/never-deleted posture throughout), so a missing one is
            // a permanent inconsistency to surface, not a race to wait out.
            return OutboxHandlingResult.Permanent($"Ticket {payload.TicketId} no longer exists.");
        }

        // Guard 1 — the domain's write-once acknowledgement timestamp. A
        // redelivered message for an already-acknowledged ticket stops here,
        // before any recipient is resolved and before the provider is
        // touched.
        if (ticket.AcknowledgementSentAtUtc is not null)
        {
            return OutboxHandlingResult.Succeeded();
        }

        var notification = await notificationRepository.GetByOutboxMessageAsync(
            message.OutboxMessageId, NotificationType.Acknowledgement, cancellationToken);

        // Guard 2 — a delivery row that already reached a terminal state.
        if (notification is { IsTerminal: true })
        {
            return notification.DeliveryStatus == NotificationDeliveryStatus.Sent
                ? OutboxHandlingResult.Succeeded()
                : OutboxHandlingResult.Permanent("The notification for this message was already dead-lettered.");
        }

        var snapshot = await snapshotRepository.GetByTicketIdAsync(ticket.TicketId, cancellationToken);
        var recipient = AcknowledgementRecipientResolver.Resolve(snapshot);

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        if (notification is null)
        {
            notification = new Notification(
                ticket.TicketId,
                NotificationType.Acknowledgement,
                // Always null: the acknowledgement goes to the external
                // requester, never to a staff mailbox (MVP-Data-Dictionary.md
                // §2.21's "Null for an external recipient").
                recipientEmployeeId: null,
                recipient.Address,
                NotificationChannel.Email,
                message.CorrelationId,
                message.OutboxMessageId,
                nowUtc);

            await notificationRepository.AddAsync(notification, cancellationToken);
        }

        if (!recipient.IsDeliverable)
        {
            // The reported CRITICAL gap: no approved column holds a
            // requester's email address (see AcknowledgementRecipientResolver).
            // Permanent rather than transient — no number of retries will make
            // a phone number into an address — and dead-lettered rather than
            // dropped, so the ticket that could not be acknowledged is
            // visible, countable and auditable.
            notification.DeadLetter();

            await auditWriter.WriteAsync(
                actorEmployeeId: null,
                NotificationAuditActions.NotificationDeadLettered,
                NotificationAuditActions.NotificationEntityType,
                ticket.TicketId.ToString(CultureInfo.InvariantCulture),
                beforeValue: "DeliveryStatus=Pending",
                afterValue: $"DeliveryStatus=DeadLettered;Reason=NoDeliverableRecipient;OutboxMessageId={message.OutboxMessageId}",
                message.CorrelationId,
                cancellationToken);

            return OutboxHandlingResult.Permanent(recipient.Reason ?? "No deliverable recipient.");
        }

        var department = await departmentRepository.GetByIdAsync(ticket.CurrentDepartmentId, cancellationToken);

        // Absent for a provisional ticket whose SLA clock has not started
        // (FR-TKT-09) — the body omits the line rather than inventing a time.
        var slaInstance = await slaInstanceRepository.GetCurrentAsync(ticket.TicketId, cancellationToken);

        var email = new EmailMessage(
            recipient.Address!,
            TicketAcknowledgementContent.Subject(ticket.TicketNumber),
            TicketAcknowledgementContent.Body(
                ticket.TicketNumber,
                ticket.CreatedAtUtc,
                department?.Name ?? "Customer Service",
                slaInstance?.FirstResponseDueAtUtc),
            message.CorrelationId);

        EmailSendResult sendResult;
        try
        {
            sendResult = await emailSender.SendAsync(email, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An adapter that throws instead of classifying is treated as
            // transient — the safer direction: a genuinely permanent failure
            // costs a bounded number of extra attempts before dead-lettering,
            // whereas mis-classifying a transient outage as permanent loses
            // the acknowledgement outright. The exception type is recorded,
            // never its message, which could carry provider-echoed content.
            sendResult = EmailSendResult.Transient($"Email provider threw {ex.GetType().Name}.");
        }

        return sendResult.Outcome switch
        {
            EmailSendOutcome.Sent => await RecordSuccessAsync(ticket, notification, message, nowUtc, cancellationToken),
            EmailSendOutcome.PermanentFailure => await RecordPermanentFailureAsync(
                ticket.TicketId, notification, message, sendResult.Error, cancellationToken),
            _ => await RecordTransientFailureAsync(ticket.TicketId, notification, message, sendResult.Error, cancellationToken)
        };
    }

    private async Task<OutboxHandlingResult> RecordSuccessAsync(
        Domain.Modules.Ticketing.Ticket ticket,
        Notification notification,
        OutboxMessage message,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        // The only ticket field this whole path writes. Not
        // FirstHumanResponseAtUtc, not SlaState — see this class's remarks.
        ticket.RecordAcknowledgementSent(nowUtc);
        notification.MarkSent();

        await auditWriter.WriteAsync(
            actorEmployeeId: null,
            NotificationAuditActions.NotificationDeliverySucceeded,
            NotificationAuditActions.NotificationEntityType,
            ticket.TicketId.ToString(CultureInfo.InvariantCulture),
            beforeValue: "DeliveryStatus=Pending",
            afterValue: $"DeliveryStatus=Sent;Channel=Email;OutboxMessageId={message.OutboxMessageId}",
            message.CorrelationId,
            cancellationToken);

        return OutboxHandlingResult.Succeeded();
    }

    private async Task<OutboxHandlingResult> RecordTransientFailureAsync(
        long ticketId, Notification notification, OutboxMessage message, string? error, CancellationToken cancellationToken)
    {
        notification.RecordFailedAttempt();

        await auditWriter.WriteAsync(
            actorEmployeeId: null,
            NotificationAuditActions.NotificationDeliveryFailed,
            NotificationAuditActions.NotificationEntityType,
            ticketId.ToString(CultureInfo.InvariantCulture),
            beforeValue: null,
            afterValue: $"DeliveryStatus=Failed;RetryCount={notification.RetryCount};OutboxMessageId={message.OutboxMessageId}",
            message.CorrelationId,
            cancellationToken);

        return OutboxHandlingResult.Transient(error ?? "The email provider reported a transient failure.");
    }

    private async Task<OutboxHandlingResult> RecordPermanentFailureAsync(
        long ticketId, Notification notification, OutboxMessage message, string? error, CancellationToken cancellationToken)
    {
        notification.RecordFailedAttempt();
        notification.DeadLetter();

        await auditWriter.WriteAsync(
            actorEmployeeId: null,
            NotificationAuditActions.NotificationDeadLettered,
            NotificationAuditActions.NotificationEntityType,
            ticketId.ToString(CultureInfo.InvariantCulture),
            beforeValue: "DeliveryStatus=Failed",
            afterValue: $"DeliveryStatus=DeadLettered;Reason=PermanentProviderFailure;OutboxMessageId={message.OutboxMessageId}",
            message.CorrelationId,
            cancellationToken);

        return OutboxHandlingResult.Permanent(error ?? "The email provider reported a permanent failure.");
    }
}

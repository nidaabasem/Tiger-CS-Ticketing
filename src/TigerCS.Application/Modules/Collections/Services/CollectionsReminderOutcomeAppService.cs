using System.Text.Json;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>
/// Delivery events and customer responses reported back for a reminder.
///
/// <para>
/// <b>Idempotent per event.</b> Each callback carries the caller's own
/// <c>eventId</c>; (reminder, eventId) is unique, so a resend is answered
/// with what was stored the first time and records nothing twice.
/// </para>
///
/// <para>
/// <b>A response in a conversation gets a ticket through the existing
/// Genesys ingestion</b> — <see cref="GenesysInquiryIngestionAppService"/>,
/// idempotent on the conversation id, routed by the configured Collections
/// department code or Genesys queue, created Unclassified under the existing
/// classification and lifecycle rules. Nothing here classifies, resolves or
/// closes a ticket. "I already paid", a dispute, a request for a human and
/// an AI disconnection each raise human follow-up through the existing
/// handoff path (<see cref="GenesysTicketUpdateAppService"/>); nothing posts
/// a payment or changes a balance.
/// </para>
///
/// <para>
/// <b>Durable.</b> The response row and an outbox message are committed
/// together before the ticket is attempted. If ingestion fails (Genesys
/// switched off, routing unconfigured, a transient database error) the
/// response stays recorded with its ticket Pending, and the outbox retries
/// it (<see cref="CollectionsResponseTicketHandler"/>) until it links or is
/// dead-lettered for an operator.
/// </para>
/// </summary>
public sealed class CollectionsReminderOutcomeAppService(
    CollectionsOptions options,
    CollectionsAuthorizationService authorization,
    CollectionsClock clock,
    ICollectionsReminderRepository reminderRepository,
    ICollectionsUnitOfWork unitOfWork,
    IOutboxWriter outboxWriter,
    IAuditEntryWriter auditWriter,
    GenesysInquiryIngestionAppService ingestion,
    GenesysTicketUpdateAppService ticketUpdates,
    ILogger<CollectionsReminderOutcomeAppService> logger)
{
    public const string ResponseTicketEventType = "CollectionsReminderResponseTicket";

    public async Task<CollectionsResult<RecordReminderOutcomeResponseDto>> RecordAsync(
        CollectionsCaller caller, long reminderId, RecordReminderOutcomeRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!options.Enabled)
        {
            return Fail(CollectionsOutcome.Disabled);
        }

        var permissions = await authorization.ResolveAsync(caller, cancellationToken);
        if (!permissions.CanReportOutcomes)
        {
            return Fail(CollectionsOutcome.Forbidden, "Only a configured integration account may report reminder outcomes.");
        }

        if (string.IsNullOrWhiteSpace(request.EventId) || request.EventId.Trim().Length > CollectionsReminderEvent.ExternalEventIdMaxLength
            || request.EventId.Trim().StartsWith("tigercs:", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(CollectionsOutcome.ValidationFailed, $"eventId is required, at most {CollectionsReminderEvent.ExternalEventIdMaxLength} characters, and may not start with 'tigercs:'.");
        }

        if (!CollectionsEnums.TryParse<ReminderEventType>(request.Outcome, out var eventType)
            || eventType is not (ReminderEventType.Sent or ReminderEventType.Delivered or ReminderEventType.Failed or ReminderEventType.CustomerResponded))
        {
            return Fail(CollectionsOutcome.ValidationFailed, "outcome must be Sent, Delivered, Failed or CustomerResponded.");
        }

        CustomerResponseKind kind = default;
        if (eventType == ReminderEventType.CustomerResponded)
        {
            if (request.Response is null || !CollectionsEnums.TryParse(request.Response.Kind, out kind))
            {
                return Fail(CollectionsOutcome.ValidationFailed, $"response.kind is required with CustomerResponded and must be one of {CollectionsEnums.Names<CustomerResponseKind>()}.");
            }

            if (request.Response.PromisedAmount is <= 0m)
            {
                return Fail(CollectionsOutcome.ValidationFailed, "response.promisedAmount must be positive when supplied.");
            }

            if (request.Response.ConversationId is { Length: > CollectionsReminderEvent.ConversationIdMaxLength })
            {
                return Fail(CollectionsOutcome.ValidationFailed, $"response.conversationId is at most {CollectionsReminderEvent.ConversationIdMaxLength} characters.");
            }
        }

        var reminder = await reminderRepository.GetByIdAsync(reminderId, cancellationToken);
        if (reminder is null)
        {
            return Fail(CollectionsOutcome.ReminderNotFound, $"No reminder {reminderId}.");
        }

        var eventId = request.EventId.Trim();
        if (reminder.Events.FirstOrDefault(e => e.ExternalEventId == eventId) is { } already)
        {
            return CollectionsResult<RecordReminderOutcomeResponseDto>.Ok(Response("AlreadyRecorded", reminder, already));
        }

        if (reminder.Status == ReminderStatus.Suppressed)
        {
            return Fail(CollectionsOutcome.Conflict, $"Reminder {reminderId} was suppressed before dispatch and was never sent; it accepts no outcomes.");
        }

        var now = clock.UtcNow;
        var occurredAt = request.OccurredAtUtc?.ToUniversalTime() ?? now;
        CollectionsReminderEvent recorded;
        var correlationId = Guid.NewGuid();

        if (eventType == ReminderEventType.CustomerResponded)
        {
            var response = request.Response!;
            recorded = reminder.AddEvent(CollectionsReminderEvent.Response(
                reminder, eventId, kind, occurredAt, now, caller.EmployeeId,
                response.ConversationId, response.CustomerPhone, response.Note,
                response.PromisedPaymentDate, response.PromisedAmount));

            if (recorded.TicketStatus == ResponseTicketStatus.Pending)
            {
                await outboxWriter.WriteAsync(
                    ResponseTicketEventType,
                    JsonSerializer.Serialize(new ResponseTicketPayload(reminderId, eventId, caller.EmployeeId)),
                    correlationId,
                    idempotencyKey: $"collections-response-ticket:{reminderId}:{eventId}",
                    now,
                    cancellationToken);
            }
        }
        else
        {
            reminder.ApplyDelivery(eventType, occurredAt, request.FailureReason, request.ProviderReference);
            recorded = reminder.AddEvent(CollectionsReminderEvent.Delivery(
                reminder, eventId, eventType, occurredAt, now, caller.EmployeeId,
                eventType == ReminderEventType.Failed ? request.FailureReason : request.ProviderReference));
        }

        await auditWriter.WriteAsync(
            caller.EmployeeId, "CollectionsReminderOutcomeRecorded", CollectionsReminderAppService.AuditEntityType,
            CollectionsMapper.AuditEntityId(reminder.DeduplicationKey), null,
            $"ReminderId={reminderId};EventId={eventId};Outcome={eventType}{(eventType == ReminderEventType.CustomerResponded ? $";Response={kind}" : "")};Status={reminder.Status}",
            correlationId, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateWriteException)
        {
            // The same event id raced in concurrently and won.
            unitOfWork.DiscardPendingChanges();
            var reloaded = await reminderRepository.GetByIdAsync(reminderId, cancellationToken);
            var winner = reloaded?.Events.FirstOrDefault(e => e.ExternalEventId == eventId);
            if (reloaded is null || winner is null)
            {
                throw;
            }

            return CollectionsResult<RecordReminderOutcomeResponseDto>.Ok(Response("AlreadyRecorded", reloaded, winner));
        }

        if (recorded.TicketStatus == ResponseTicketStatus.Pending)
        {
            // Best effort now; the outbox message already guarantees a retry.
            await ProcessResponseTicketAsync(reminderId, eventId, caller.EmployeeId, cancellationToken);
            reminder = await reminderRepository.GetByIdAsync(reminderId, cancellationToken) ?? reminder;
            recorded = reminder.Events.First(e => e.ExternalEventId == eventId);
        }

        var dto = Response("Recorded", reminder, recorded);
        return recorded.TicketStatus == ResponseTicketStatus.Pending
            ? CollectionsResult<RecordReminderOutcomeResponseDto>.Ok(dto, CollectionsOutcome.Accepted)
            : CollectionsResult<RecordReminderOutcomeResponseDto>.Ok(dto);
    }

    /// <summary>
    /// Creates or reuses the response's ticket and raises any human
    /// follow-up, then links the ticket. Safe to run any number of times,
    /// concurrently too: ingestion is idempotent on the conversation id, the
    /// handoff on the conversation's outstanding work, and the link on the
    /// ticket. The link is written last, so a response is only ever marked
    /// Linked once its follow-up exists.
    /// </summary>
    public async Task<ResponseTicketProcessingResult> ProcessResponseTicketAsync(
        long reminderId, string eventId, Guid actorEmployeeId, CancellationToken cancellationToken = default)
    {
        var reminder = await reminderRepository.GetByIdAsync(reminderId, cancellationToken);
        var response = reminder?.Events.FirstOrDefault(e => e.ExternalEventId == eventId && e.EventType == ReminderEventType.CustomerResponded);
        if (reminder is null || response is null)
        {
            return new ResponseTicketProcessingResult(ResponseTicketProcessingOutcome.Gone, $"Reminder {reminderId} event '{eventId}' no longer exists.");
        }

        if (response.TicketStatus != ResponseTicketStatus.Pending)
        {
            return new ResponseTicketProcessingResult(ResponseTicketProcessingOutcome.Done);
        }

        string? failure;
        try
        {
            failure = await CreateOrReuseTicketAsync(reminder, response, actorEmployeeId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Ticket for Collections reminder {ReminderId} response {EventId} failed; it stays pending for retry.", reminderId, eventId);
            failure = $"Ticket creation threw {ex.GetType().Name}.";
            unitOfWork.DiscardPendingChanges();
            reminder = await reminderRepository.GetByIdAsync(reminderId, cancellationToken);
            response = reminder?.Events.FirstOrDefault(e => e.ExternalEventId == eventId);
            if (response is null)
            {
                return new ResponseTicketProcessingResult(ResponseTicketProcessingOutcome.Pending, failure);
            }
        }

        if (failure is not null)
        {
            response.RecordTicketAttemptFailed(failure);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new ResponseTicketProcessingResult(ResponseTicketProcessingOutcome.Pending, failure);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new ResponseTicketProcessingResult(ResponseTicketProcessingOutcome.Done);
    }

    private async Task<string?> CreateOrReuseTicketAsync(
        CollectionsReminder reminder, CollectionsReminderEvent response, Guid actorEmployeeId, CancellationToken cancellationToken)
    {
        var routing = options.ResponseTickets;
        var queueId = string.IsNullOrWhiteSpace(routing.QueueId) ? null : routing.QueueId.Trim();
        var departmentCode = queueId is null && !string.IsNullOrWhiteSpace(routing.DepartmentCode) ? routing.DepartmentCode.Trim() : null;
        if (queueId is null && departmentCode is null)
        {
            return "No Collections ticket routing is configured (Collections:ResponseTickets:DepartmentCode or QueueId).";
        }

        var ingest = await ingestion.IngestAsync(actorEmployeeId, new GenesysInquiryDto(
            ConversationId: response.ConversationId!,
            Channel: GenesysChannel.Phone,
            Direction: "Outbound",
            CustomerPhone: response.CustomerPhone,
            QueueId: queueId,
            DepartmentCode: departmentCode,
            Subject: $"Collections reminder response: {Describe(response.ResponseKind!.Value)} (account {reminder.AccountId}, reminder {reminder.CollectionsReminderId})"),
            cancellationToken);

        if (ingest.Outcome is not (GenesysIngestionOutcome.TicketCreated or GenesysIngestionOutcome.AlreadyIngested) || ingest.Ticket is null)
        {
            return $"Genesys ingestion answered {ingest.Outcome}{(ingest.Detail is null ? "" : $": {ingest.Detail}")}.";
        }

        if (response.HumanFollowUpRequired)
        {
            var update = await ticketUpdates.UpdateAsync(actorEmployeeId, ingest.Ticket.TicketId, new GenesysTicketUpdateDto(
                response.ConversationId!,
                Handoff: new GenesysHandoffUpdateDto(
                    Required: true,
                    Reason: FollowUpReason(reminder, response),
                    Trigger: response.ResponseKind switch
                    {
                        CustomerResponseKind.RequestedHuman => "CustomerRequestedHuman",
                        CustomerResponseKind.AiDisconnected => "AiConnectionLost",
                        _ => "AiEscalated"
                    })),
                cancellationToken);

            if (update.Outcome != GenesysTicketUpdateOutcome.Applied)
            {
                return $"Human follow-up could not be recorded: {update.Outcome}{(update.Detail is null ? "" : $" — {update.Detail}")}.";
            }
        }

        response.LinkTicket(ingest.Ticket.TicketId, ingest.Ticket.TicketNumber, clock.UtcNow);
        return null;
    }

    private static string FollowUpReason(CollectionsReminder reminder, CollectionsReminderEvent response) => response.ResponseKind switch
    {
        CustomerResponseKind.AlreadyPaid =>
            $"Payment verification: the customer says the payment for account {reminder.AccountId} was already made. "
            + "Verify it in the financial source. No payment has been posted and no balance has changed.",
        CustomerResponseKind.Disputed =>
            $"The customer disputes the reminder amount ({reminder.Amount} {reminder.Currency}) for account {reminder.AccountId}.",
        CustomerResponseKind.RequestedHuman =>
            $"The customer asked for a person during the payment reminder for account {reminder.AccountId}.",
        _ =>
            $"The voice bot lost the payment reminder call for account {reminder.AccountId}; follow up with the customer.",
    };

    private static string Describe(CustomerResponseKind kind) => kind switch
    {
        CustomerResponseKind.PromiseToPay => "promise to pay",
        CustomerResponseKind.AlreadyPaid => "customer says already paid",
        CustomerResponseKind.RequestedHuman => "customer requested a human",
        CustomerResponseKind.AiDisconnected => "AI disconnected",
        CustomerResponseKind.Disputed => "amount disputed",
        _ => "other"
    };

    private static RecordReminderOutcomeResponseDto Response(string outcome, CollectionsReminder reminder, CollectionsReminderEvent e) =>
        new(outcome, reminder.CollectionsReminderId, reminder.Status.ToString(), CollectionsMapper.ToDto(e));

    private static CollectionsResult<RecordReminderOutcomeResponseDto> Fail(CollectionsOutcome outcome, string? detail = null) =>
        CollectionsResult<RecordReminderOutcomeResponseDto>.Fail(outcome, detail);

    internal sealed record ResponseTicketPayload(long ReminderId, string EventId, Guid ActorEmployeeId);
}

public enum ResponseTicketProcessingOutcome
{
    /// <summary>Linked (now or earlier), or no ticket was owed.</summary>
    Done,

    /// <summary>Still owed; will be retried.</summary>
    Pending,

    /// <summary>The reminder or event no longer exists — nothing to retry.</summary>
    Gone
}

public sealed record ResponseTicketProcessingResult(ResponseTicketProcessingOutcome Outcome, string? Detail = null);

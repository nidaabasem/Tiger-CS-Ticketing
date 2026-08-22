using System.Globalization;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Domain.Infrastructure;

namespace TigerCS.Application.Modules.Notifications.Services;

/// <summary>
/// ADR-0013's dispatcher, driven by ADR-0015's recurring Hangfire job. Reads
/// pending <c>OutboxMessages</c> and hands each to the handler registered for
/// its <c>EventType</c>.
///
/// <para>
/// <b>Owns reliability; handlers own meaning.</b> Claiming, attempt
/// accounting, backoff, dead-lettering and the audit trail live here, once,
/// for every event type — which is exactly ADR-0013's stated advantage over a
/// bespoke reliability mechanism per integration.
/// </para>
///
/// <para>
/// <b>Two commits per message, and the provider call sits between them.</b>
/// The claim commits first so no second worker can take the same message;
/// then the handler runs, including its network call, with no transaction
/// held; then the outcome commits. Sending inside a transaction would pin
/// database locks for the provider's latency.
/// </para>
///
/// <para>
/// <b>Nothing is ever discarded.</b> A message either becomes
/// <c>Processed</c>, stays <c>Pending</c> for another attempt, or becomes
/// <c>DeadLettered</c> and is kept for review (FR-NOT-05). There is no branch
/// that deletes a row or silently swallows a failure — an unexpected
/// exception is caught only so one bad message cannot stall the batch, and it
/// is recorded on the message as a failed attempt like any other.
/// </para>
/// </summary>
public sealed class OutboxDispatcher(
    IOutboxMessageRepository outboxRepository,
    IEnumerable<IOutboxEventHandler> handlers,
    INotificationsUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    OutboxDispatchPolicy policy,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcher> logger)
{

    public async Task<OutboxDispatchResult> DispatchPendingAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        var candidates = await outboxRepository.GetDispatchableAsync(
            nowUtc, policy.MaxAttempts, policy.BaseRetryDelay, policy.BatchSize, cancellationToken);

        var result = new OutboxDispatchResult();

        foreach (var message in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DispatchOneAsync(message, result, cancellationToken);
        }

        return result;
    }

    private async Task DispatchOneAsync(OutboxMessage message, OutboxDispatchResult result, CancellationToken cancellationToken)
    {
        // The claim. Attempts is a concurrency token, so this save is a
        // compare-and-swap: if a second worker already claimed this message
        // between our read and this write, our UPDATE matches zero rows and
        // TrySaveClaimAsync reports the loss. That is the mechanism behind
        // "concurrent workers never deliver the same message twice".
        message.BeginAttempt();

        if (!await unitOfWork.TrySaveClaimAsync(cancellationToken))
        {
            result.Skipped++;
            return;
        }

        var handler = handlers.FirstOrDefault(h =>
            string.Equals(h.EventType, message.EventType, StringComparison.Ordinal));

        OutboxHandlingResult handled;
        if (handler is null)
        {
            // An event type nobody consumes yet. Permanent rather than an
            // endless retry, and dead-lettered rather than dropped, so the
            // unconsumed event is visible instead of quietly accumulating as
            // pending work nothing will ever do.
            handled = OutboxHandlingResult.Permanent($"No handler is registered for event type '{message.EventType}'.");
        }
        else
        {
            try
            {
                handled = await handler.HandleAsync(message, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Caught so one poisoned message cannot stall the batch. The
                // exception type is recorded; its message is not, since a
                // handler exception can carry provider- or data-derived text
                // (Security-Architecture.md §11).
                logger.LogError(
                    ex,
                    "Outbox message {OutboxMessageId} ({EventType}) threw during handling. CorrelationId {CorrelationId}.",
                    message.OutboxMessageId, message.EventType, message.CorrelationId);

                await unitOfWork.DiscardPendingChangesAsync(cancellationToken);

                var reloaded = await outboxRepository.GetByIdAsync(message.OutboxMessageId, cancellationToken);
                if (reloaded is null)
                {
                    result.Failed++;
                    return;
                }

                message = reloaded;
                handled = OutboxHandlingResult.Transient($"Handler threw {ex.GetType().Name}.");
            }
        }

        await ApplyOutcomeAsync(message, handled, result, cancellationToken);
    }

    private async Task ApplyOutcomeAsync(
        OutboxMessage message, OutboxHandlingResult handled, OutboxDispatchResult result, CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        switch (handled.Outcome)
        {
            case OutboxHandlingOutcome.Succeeded:
                message.MarkProcessed(nowUtc);
                result.Processed++;
                break;

            case OutboxHandlingOutcome.PermanentFailure:
                message.DeadLetter(handled.Error ?? "Permanent failure.", nowUtc);
                result.DeadLettered++;
                await WriteDeadLetterAuditAsync(message, "PermanentFailure", cancellationToken);
                break;

            default:
                var retryable = message.RecordTransientFailure(handled.Error ?? "Transient failure.", policy.MaxAttempts, nowUtc);
                if (retryable)
                {
                    result.Retried++;
                    await WriteRetryScheduledAuditAsync(message, cancellationToken);
                }
                else
                {
                    result.DeadLettered++;
                    await WriteDeadLetterAuditAsync(message, "AttemptsExhausted", cancellationToken);
                }

                break;
        }

        try
        {
            // One save covering both the handler's own changes (the
            // Notifications row, the ticket's acknowledgement timestamp, the
            // audit entries) and this outcome — so a message can never be
            // marked Processed without the effect it describes being
            // committed alongside it.
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The outcome could not be persisted. The claim already committed,
            // so Attempts is durably incremented and the message stays
            // Pending — it will be retried, bounded by the same attempt
            // budget, and cannot spin forever.
            logger.LogError(
                ex,
                "Failed to persist the dispatch outcome for Outbox message {OutboxMessageId}. CorrelationId {CorrelationId}.",
                message.OutboxMessageId, message.CorrelationId);

            await unitOfWork.DiscardPendingChangesAsync(cancellationToken);
            result.Failed++;
        }
    }

    private Task WriteRetryScheduledAuditAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var nextEligible = OutboxMessage.NextEligibleAtUtc(message.OccurredAtUtc, message.Attempts, policy.BaseRetryDelay);

        return auditWriter.WriteAsync(
            actorEmployeeId: null,
            NotificationAuditActions.NotificationRetryScheduled,
            NotificationAuditActions.OutboxMessageEntityType,
            message.OutboxMessageId.ToString(),
            beforeValue: null,
            afterValue: string.Create(
                CultureInfo.InvariantCulture,
                $"Attempts={message.Attempts};MaxAttempts={policy.MaxAttempts};NextEligibleAtUtc={nextEligible:O}"),
            message.CorrelationId,
            cancellationToken);
    }

    private Task WriteDeadLetterAuditAsync(OutboxMessage message, string reason, CancellationToken cancellationToken) =>
        auditWriter.WriteAsync(
            actorEmployeeId: null,
            NotificationAuditActions.NotificationDeadLettered,
            NotificationAuditActions.OutboxMessageEntityType,
            message.OutboxMessageId.ToString(),
            beforeValue: "Status=Pending",
            afterValue: string.Create(
                CultureInfo.InvariantCulture,
                $"Status=DeadLettered;Reason={reason};Attempts={message.Attempts};EventType={message.EventType}"),
            message.CorrelationId,
            cancellationToken);
}

/// <summary>What one dispatcher pass did. Counts only — no identifiers, no payloads — so it is safe to log verbatim.</summary>
public sealed class OutboxDispatchResult
{
    /// <summary>Delivered (or recognised as already delivered) and marked <c>Processed</c>.</summary>
    public int Processed { get; set; }

    /// <summary>Failed transiently with attempts remaining; still <c>Pending</c>.</summary>
    public int Retried { get; set; }

    /// <summary>Moved to <c>DeadLettered</c> — retried out, or failed in a way no retry could fix. Retained and visible.</summary>
    public int DeadLettered { get; set; }

    /// <summary>Another worker held the claim. Not a failure.</summary>
    public int Skipped { get; set; }

    /// <summary>The outcome itself could not be persisted; the message stays <c>Pending</c> for a later pass.</summary>
    public int Failed { get; set; }

    public int Total => Processed + Retried + DeadLettered + Skipped + Failed;
}

using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Notifications.Services;

/// <summary>
/// ADR-0013's dispatcher: claiming, retry accounting, backoff,
/// dead-lettering, and the guarantee that nothing is ever silently
/// discarded.
/// </summary>
public class OutboxDispatcherTests
{
    [Fact]
    public async Task PendingMessage_IsDispatchedAndMarkedProcessed()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync();
        var message = f.EnqueueTicketCreated(ticket.TicketId);

        var result = await f.CreateDispatcher().DispatchPendingAsync();

        Assert.Equal(1, result.Processed);
        Assert.Equal(OutboxMessageStatus.Processed, message.Status);
        Assert.Equal(1, message.Attempts);
        Assert.NotNull(message.ProcessedAtUtc);
        Assert.Single(f.Email.Sent);
    }

    /// <summary>
    /// A processed message is not eligible again, so a second pass is a no-op
    /// — the dispatcher-level half of "duplicate processing does not send
    /// twice".
    /// </summary>
    [Fact]
    public async Task ProcessedMessage_IsNotDispatchedAgainOnALaterPass()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync();
        f.EnqueueTicketCreated(ticket.TicketId);
        var dispatcher = f.CreateDispatcher();

        await dispatcher.DispatchPendingAsync();
        f.Time.Advance(TimeSpan.FromHours(1));
        var second = await dispatcher.DispatchPendingAsync();

        Assert.Equal(0, second.Total);
        Assert.Single(f.Email.Sent);
    }

    /// <summary>
    /// The concurrent-worker case. <c>Attempts</c> is an optimistic-concurrency
    /// token, so persisting a claim is a compare-and-swap: the worker that
    /// loses it must send nothing at all, not merely avoid marking the message
    /// processed.
    /// </summary>
    [Fact]
    public async Task LostClaimRace_SendsNothing()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync();
        f.EnqueueTicketCreated(ticket.TicketId);
        f.UnitOfWork.ClaimsToLose = 1;

        var result = await f.CreateDispatcher().DispatchPendingAsync();

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Processed);
        Assert.Empty(f.Email.Sent);
        Assert.Null(ticket.AcknowledgementSentAtUtc);
        Assert.Empty(f.Notifications.Notifications);
    }

    /// <summary>
    /// Two dispatchers over one message and one claim: exactly one delivers.
    /// This is the property that makes running more than one Hangfire worker
    /// safe.
    /// </summary>
    [Fact]
    public async Task TwoWorkersOneMessage_ExactlyOneDelivers()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync();
        f.EnqueueTicketCreated(ticket.TicketId);

        var winner = f.CreateDispatcher();

        // The second worker's claim loses the compare-and-swap, exactly as it
        // would when its UPDATE matched zero rows.
        f.UnitOfWork.ClaimsToLose = 0;
        var first = await winner.DispatchPendingAsync();

        f.UnitOfWork.ClaimsToLose = 1;
        var second = await f.CreateDispatcher().DispatchPendingAsync();

        Assert.Equal(1, first.Processed);
        Assert.Equal(0, second.Processed);
        Assert.Single(f.Email.Sent);
        Assert.Single(f.Notifications.Notifications);
    }

    [Fact]
    public async Task TransientFailure_StaysPendingAndBecomesEligibleOnlyAfterBackoff()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync();
        var message = f.EnqueueTicketCreated(ticket.TicketId);
        f.Email.ThenTransient();

        var dispatcher = f.CreateDispatcher();
        var first = await dispatcher.DispatchPendingAsync();

        Assert.Equal(1, first.Retried);
        Assert.Equal(OutboxMessageStatus.Pending, message.Status);
        Assert.Equal(1, message.Attempts);
        Assert.NotNull(message.LastError);

        // Immediately re-running must find nothing: the backoff has not
        // elapsed, so a busy dispatcher cannot burn the whole attempt budget
        // in one pass.
        var tooSoon = await dispatcher.DispatchPendingAsync();
        Assert.Equal(0, tooSoon.Total);
        Assert.Equal(1, f.Email.SendCount);

        // 30s base, so attempt 2 becomes eligible 30s after the event.
        f.Time.Advance(TimeSpan.FromSeconds(31));
        var afterBackoff = await dispatcher.DispatchPendingAsync();

        Assert.Equal(1, afterBackoff.Processed);
        Assert.Equal(OutboxMessageStatus.Processed, message.Status);
        Assert.Equal(2, message.Attempts);
        Assert.Equal(2, f.Email.SendCount);
    }

    /// <summary>
    /// The retry budget is finite and ends in a dead-letter, never in an
    /// endless retry and never in a discard (FR-NOT-05).
    /// </summary>
    [Fact]
    public async Task RepeatedTransientFailures_DeadLetterAfterTheAttemptThreshold()
    {
        var f = new NotificationServiceFixture { Policy = new OutboxDispatchPolicy(3, TimeSpan.FromSeconds(30), 50) };
        var ticket = await f.SeedVerifiedTicketAsync();
        var message = f.EnqueueTicketCreated(ticket.TicketId);
        f.Email.DefaultResult = Application.Modules.Notifications.Abstractions.EmailSendResult.Transient("provider down");

        var dispatcher = f.CreateDispatcher();

        for (var pass = 0; pass < 3; pass++)
        {
            await dispatcher.DispatchPendingAsync();
            f.Time.Advance(TimeSpan.FromHours(1));
        }

        Assert.Equal(OutboxMessageStatus.DeadLettered, message.Status);
        Assert.Equal(3, message.Attempts);
        Assert.Equal(3, f.Email.SendCount);

        // Retained and visible, not deleted.
        Assert.Contains(message, f.OutboxMessages.Messages);
        Assert.NotNull(message.LastError);
        Assert.Equal(NotificationDeliveryStatus.Failed, Assert.Single(f.Notifications.Notifications).DeliveryStatus);

        // And genuinely terminal — no later pass picks it up again.
        f.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, (await dispatcher.DispatchPendingAsync()).Total);
        Assert.Equal(3, f.Email.SendCount);
    }

    [Fact]
    public async Task DeadLettering_WritesTheApprovedAuditEvents()
    {
        var f = new NotificationServiceFixture { Policy = new OutboxDispatchPolicy(2, TimeSpan.FromSeconds(30), 50) };
        var ticket = await f.SeedVerifiedTicketAsync();
        f.EnqueueTicketCreated(ticket.TicketId);
        f.Email.DefaultResult = Application.Modules.Notifications.Abstractions.EmailSendResult.Transient("provider down");

        var dispatcher = f.CreateDispatcher();
        await dispatcher.DispatchPendingAsync();
        f.Time.Advance(TimeSpan.FromHours(1));
        await dispatcher.DispatchPendingAsync();

        // Retry scheduled on the attempt that had budget left, dead-lettered
        // on the one that did not.
        var retry = Assert.Single(f.Audit.Entries, e => e.Action == NotificationAuditActions.NotificationRetryScheduled);
        Assert.Contains("NextEligibleAtUtc=", retry.AfterValue, StringComparison.Ordinal);

        var deadLetter = Assert.Single(
            f.Audit.Entries,
            e => e.Action == NotificationAuditActions.NotificationDeadLettered
                && e.EntityType == NotificationAuditActions.OutboxMessageEntityType);
        Assert.Contains("AttemptsExhausted", deadLetter.AfterValue, StringComparison.Ordinal);
    }

    /// <summary>An event type nobody consumes must be visible, not accumulate silently as pending work nothing will ever do.</summary>
    [Fact]
    public async Task UnhandledEventType_DeadLettersRatherThanRetryingForever()
    {
        var f = new NotificationServiceFixture();
        var record = new IdempotencyRecord("Ticket:1:SomethingElse:v1", IdempotencyScopes.OutboxDispatch, f.Time.GetUtcNow().UtcDateTime);
        var message = new OutboxMessage(
            Guid.NewGuid(), "SomeFutureEvent", "{}", Guid.NewGuid(), record, f.Time.GetUtcNow().UtcDateTime);
        f.OutboxMessages.Messages.Add(message);

        var result = await f.CreateDispatcher().DispatchPendingAsync();

        Assert.Equal(1, result.DeadLettered);
        Assert.Equal(OutboxMessageStatus.DeadLettered, message.Status);
        Assert.Contains("No handler is registered", message.LastError!, StringComparison.Ordinal);
    }

    /// <summary>One poisoned message must not stall the rest of the batch.</summary>
    [Fact]
    public async Task HandlerThatThrows_IsRecordedAsAFailedAttemptAndTheBatchContinues()
    {
        var f = new NotificationServiceFixture();
        var ticketA = await f.SeedVerifiedTicketAsync(ticketNumber: "TG-CS-20260822-0001");
        var ticketB = await f.SeedVerifiedTicketAsync(ticketNumber: "TG-CS-20260822-0002");
        var messageA = f.EnqueueTicketCreated(ticketA.TicketId);
        var messageB = f.EnqueueTicketCreated(ticketB.TicketId);

        var dispatcher = f.CreateDispatcher(new ThrowingHandler(messageA.OutboxMessageId), f.CreateHandler());
        var result = await dispatcher.DispatchPendingAsync();

        Assert.Equal(1, result.Retried);
        Assert.Equal(1, result.Processed);
        Assert.Equal(OutboxMessageStatus.Pending, messageA.Status);
        Assert.Equal(OutboxMessageStatus.Processed, messageB.Status);
        Assert.True(f.UnitOfWork.DiscardCount >= 1);
    }

    /// <summary>
    /// The residual at-least-once window, made explicit rather than claimed
    /// away: if the outcome cannot be persisted after a successful send, the
    /// claim is already durable, so the message stays Pending and is retried
    /// — bounded by the same attempt budget, never spinning forever.
    /// </summary>
    [Fact]
    public async Task OutcomeThatFailsToPersist_LeavesTheMessagePendingAndBounded()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync();
        var message = f.EnqueueTicketCreated(ticket.TicketId);
        f.UnitOfWork.SavesToFail = 1;

        var result = await f.CreateDispatcher().DispatchPendingAsync();

        Assert.Equal(1, result.Failed);
        Assert.Equal(OutboxMessageStatus.Pending, message.Status);
        Assert.Equal(1, message.Attempts);
        Assert.Equal(1, f.UnitOfWork.DiscardCount);
    }

    [Fact]
    public async Task BatchSize_BoundsOnePass()
    {
        var f = new NotificationServiceFixture { Policy = new OutboxDispatchPolicy(5, TimeSpan.FromSeconds(30), BatchSize: 2) };

        for (var i = 0; i < 5; i++)
        {
            var ticket = await f.SeedVerifiedTicketAsync(ticketNumber: $"TG-CS-20260822-{i:D4}");
            f.EnqueueTicketCreated(ticket.TicketId);
        }

        var result = await f.CreateDispatcher().DispatchPendingAsync();

        Assert.Equal(2, result.Processed);
        Assert.Equal(2, f.Email.SendCount);
    }

    private sealed class ThrowingHandler(Guid throwForMessageId) : IOutboxEventHandler
    {
        public string EventType => OutboxEventTypes.TicketCreated;

        public Task<OutboxHandlingResult> HandleAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            if (message.OutboxMessageId == throwForMessageId)
            {
                throw new InvalidOperationException("Simulated handler failure.");
            }

            return Task.FromResult(OutboxHandlingResult.Succeeded());
        }
    }
}

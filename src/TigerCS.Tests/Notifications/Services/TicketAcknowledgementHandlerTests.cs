using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Notifications.Services;

/// <summary>
/// FR-NOT-01 / backlog S-18 — the automated acknowledgement email itself:
/// what it contains, who it may go to, and the rule that it never satisfies
/// the First Response SLA.
/// </summary>
public class TicketAcknowledgementHandlerTests
{
    [Fact]
    public async Task DeliverableRecipient_SendsOnceAndRecordsTheAcknowledgement()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com");
        var message = f.EnqueueTicketCreated(ticket.TicketId);

        var result = await f.CreateHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        Assert.Single(f.Email.Sent);
        Assert.Equal("ahmed@example.com", f.Email.Sent[0].ToAddress);
        Assert.Equal(message.CorrelationId, f.Email.Sent[0].CorrelationId);

        Assert.NotNull(ticket.AcknowledgementSentAtUtc);

        var notification = Assert.Single(f.Notifications.Notifications);
        Assert.Equal(NotificationDeliveryStatus.Sent, notification.DeliveryStatus);
        Assert.Equal(NotificationType.Acknowledgement, notification.NotificationType);
        Assert.Equal(NotificationChannel.Email, notification.Channel);
        Assert.Equal("ahmed@example.com", notification.RecipientAddress);
        Assert.Equal(message.OutboxMessageId, notification.OutboxMessageId);

        // MVP-Data-Dictionary.md §2.21: "Null for an external recipient (the
        // customer, via email)". The acknowledgement never goes to staff.
        Assert.Null(notification.RecipientEmployeeId);
    }

    /// <summary>
    /// FR-SLA-05 / ISSUE-019, the increment's single most important rule: the
    /// automated acknowledgement must never count as the First Response SLA
    /// event. Asserted on the two fields that actually decide it, plus the
    /// ticket-level SLA dimension.
    /// </summary>
    [Fact]
    public async Task SuccessfulAcknowledgement_NeverSatisfiesFirstResponse()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync();
        var message = f.EnqueueTicketCreated(ticket.TicketId);

        await f.CreateHandler().HandleAsync(message);

        Assert.NotNull(ticket.AcknowledgementSentAtUtc);
        Assert.Null(ticket.FirstHumanResponseAtUtc);
        Assert.Equal(SlaState.Running, ticket.SlaState);
    }

    /// <summary>
    /// The same rule proved structurally rather than by observation: the
    /// breach processor resolves First Response from
    /// <c>FirstHumanResponseAtUtc</c> alone, so an acknowledged-but-unanswered
    /// ticket whose deadline has passed must still breach. If the
    /// acknowledgement had leaked into the first-response field, this deadline
    /// would report as met.
    /// </summary>
    [Fact]
    public async Task AcknowledgedTicket_StillBreachesItsFirstResponseDeadline()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync();
        var message = f.EnqueueTicketCreated(ticket.TicketId);

        await f.CreateHandler().HandleAsync(message);
        Assert.NotNull(ticket.AcknowledgementSentAtUtc);

        // What SlaBreachProcessor reads to decide whether the First Response
        // deadline was satisfied.
        Assert.Null(ticket.FirstHumanResponseAtUtc);
    }

    [Fact]
    public async Task Body_ContainsOnlyApprovedFields()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync(ticketNumber: "TG-CS-20260822-0042");
        var message = f.EnqueueTicketCreated(ticket.TicketId);

        await f.CreateHandler().HandleAsync(message);

        var email = Assert.Single(f.Email.Sent);
        Assert.Contains("TG-CS-20260822-0042", email.Body, StringComparison.Ordinal);
        Assert.Contains("Customer Service", email.Body, StringComparison.Ordinal);
        Assert.Contains("2026-08-22", email.Body, StringComparison.Ordinal);

        // Excluded deliberately, each for a stated reason — see
        // TicketAcknowledgementContent's remarks. RequestSummary and status
        // are gated on approvals no merged document grants; "Geyness
        // reference" has no data source at MVP.
        Assert.DoesNotContain("AC unit not cooling", email.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Geyness", email.Body, StringComparison.OrdinalIgnoreCase);

        // Never any internal detail.
        Assert.DoesNotContain("EscalationLevel", email.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Audit", email.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ahmed Al-Farsi", email.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reported CRITICAL gap. No approved column holds a requester's email
    /// address: MVP-Data-Dictionary.md §2.8 stores one untyped
    /// <c>SnapshotContactChannel</c> that Internal-CRM-API-Contract.md §5
    /// defines as "the phone/email on file", and MVP intake is phone-only. The
    /// handler must refuse to guess, and must leave a visible record rather
    /// than a silent skip.
    /// </summary>
    [Theory]
    [InlineData("+971 50 123 4567")]
    [InlineData("0501234567")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ahmed Al-Farsi")]
    [InlineData("ahmed@localhost")]
    [InlineData("not an @ address")]
    public async Task NonEmailContactChannel_DeadLettersWithoutSendingOrGuessing(string channel)
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync(channel);
        var message = f.EnqueueTicketCreated(ticket.TicketId);

        var result = await f.CreateHandler().HandleAsync(message);

        // Permanent, not transient: no number of retries turns a phone number
        // into an address.
        Assert.Equal(OutboxHandlingOutcome.PermanentFailure, result.Outcome);
        Assert.Empty(f.Email.Sent);
        Assert.Null(ticket.AcknowledgementSentAtUtc);

        // Retained and visible — never silently discarded (FR-NOT-05).
        var notification = Assert.Single(f.Notifications.Notifications);
        Assert.Equal(NotificationDeliveryStatus.DeadLettered, notification.DeliveryStatus);
        Assert.Null(notification.RecipientAddress);
        Assert.Equal(ticket.TicketId, notification.TicketId);

        var audit = Assert.Single(f.Audit.Entries, e => e.Action == NotificationAuditActions.NotificationDeadLettered);
        Assert.Contains("NoDeliverableRecipient", audit.AfterValue, StringComparison.Ordinal);

        // The rejected value is customer contact data and must not reach an
        // operator-visible diagnostic (Security-Architecture.md §11).
        if (!string.IsNullOrWhiteSpace(channel))
        {
            Assert.DoesNotContain(channel, result.Error!, StringComparison.Ordinal);
            Assert.DoesNotContain(channel, audit.AfterValue!, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A provisional (ISSUE-006) ticket has no verified requester snapshot at
    /// all. The event is still enqueued so "email attempted for every ticket"
    /// holds, and it dead-letters visibly rather than being skipped — which
    /// would make it indistinguishable from a ticket that was acknowledged.
    /// </summary>
    [Fact]
    public async Task ProvisionalTicketWithNoSnapshot_DeadLettersVisibly()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedProvisionalTicketAsync();
        var message = f.EnqueueTicketCreated(ticket.TicketId);

        var result = await f.CreateHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.PermanentFailure, result.Outcome);
        Assert.Empty(f.Email.Sent);
        Assert.Null(ticket.AcknowledgementSentAtUtc);
        Assert.Equal(NotificationDeliveryStatus.DeadLettered, Assert.Single(f.Notifications.Notifications).DeliveryStatus);
    }

    [Fact]
    public async Task AlreadyAcknowledgedTicket_ShortCircuitsWithoutSendingAgain()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync();
        var message = f.EnqueueTicketCreated(ticket.TicketId);
        var handler = f.CreateHandler();

        await handler.HandleAsync(message);
        Assert.Single(f.Email.Sent);

        // A redelivered message for the same event.
        var second = await handler.HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, second.Outcome);
        Assert.Single(f.Email.Sent);
        Assert.Single(f.Notifications.Notifications);
    }

    [Fact]
    public async Task TransientProviderFailure_RecordsAFailedAttemptAndStaysRetryable()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync();
        var message = f.EnqueueTicketCreated(ticket.TicketId);
        f.Email.ThenTransient();

        var result = await f.CreateHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.TransientFailure, result.Outcome);
        Assert.Null(ticket.AcknowledgementSentAtUtc);

        var notification = Assert.Single(f.Notifications.Notifications);
        Assert.Equal(NotificationDeliveryStatus.Failed, notification.DeliveryStatus);
        Assert.Equal(1, notification.RetryCount);
        Assert.False(notification.IsTerminal);

        Assert.Contains(f.Audit.Entries, e => e.Action == NotificationAuditActions.NotificationDeliveryFailed);
    }

    [Fact]
    public async Task PermanentProviderFailure_DeadLettersImmediately()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync();
        var message = f.EnqueueTicketCreated(ticket.TicketId);
        f.Email.ThenPermanent();

        var result = await f.CreateHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.PermanentFailure, result.Outcome);
        Assert.Null(ticket.AcknowledgementSentAtUtc);

        var notification = Assert.Single(f.Notifications.Notifications);
        Assert.Equal(NotificationDeliveryStatus.DeadLettered, notification.DeliveryStatus);
        Assert.True(notification.IsTerminal);
    }

    /// <summary>An adapter that throws rather than classifying is treated as transient — the safer direction, since mis-reading an outage as permanent would lose the acknowledgement outright.</summary>
    [Fact]
    public async Task ProviderThatThrows_IsTreatedAsTransient()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync();
        var message = f.EnqueueTicketCreated(ticket.TicketId);
        f.Email.ThrowOnNextSend = new TimeoutException("provider socket timeout");

        var result = await f.CreateHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.TransientFailure, result.Outcome);

        // The exception type is recorded; its message is not — a provider
        // exception can echo request content.
        Assert.Contains(nameof(TimeoutException), result.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain("socket timeout", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedPayload_IsPermanentAndSendsNothing()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync();
        var message = f.EnqueueTicketCreated(ticket.TicketId);

        typeof(TigerCS.Domain.Infrastructure.OutboxMessage)
            .GetProperty(nameof(TigerCS.Domain.Infrastructure.OutboxMessage.Payload))!
            .SetValue(message, "{ this is not json");

        var result = await f.CreateHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.PermanentFailure, result.Outcome);
        Assert.Empty(f.Email.Sent);
        Assert.Null(ticket.AcknowledgementSentAtUtc);
    }

    /// <summary>The audit trail must be able to tie a delivery back to its Outbox message and business transaction from one identifier (ADR-0014/FR-NOT-05).</summary>
    [Fact]
    public async Task AuditEntries_CarryTheOutboxMessageCorrelationId()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync();
        var correlationId = Guid.NewGuid();
        var message = f.EnqueueTicketCreated(ticket.TicketId, correlationId);

        await f.CreateHandler().HandleAsync(message);

        var audit = Assert.Single(f.Audit.Entries, e => e.Action == NotificationAuditActions.NotificationDeliverySucceeded);
        Assert.Equal(correlationId, audit.CorrelationId);
        Assert.Equal(ticket.TicketId.ToString(), audit.EntityId);
    }
}

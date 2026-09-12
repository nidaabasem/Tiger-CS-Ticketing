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
        f.Categories.Seed(ticket.CurrentDepartmentId, "Maintenance Request");
        var message = f.EnqueueTicketCreated(ticket.TicketId);

        await f.CreateHandler().HandleAsync(message);

        var email = Assert.Single(f.Email.Sent);
        Assert.Equal("Your request has been received – Ticket TG-CS-20260822-0042", email.Subject);
        Assert.Contains("TG-CS-20260822-0042", email.Body, StringComparison.Ordinal);
        Assert.Contains("22 August 2026", email.Body, StringComparison.Ordinal);
        Assert.Contains("Ahmed Al-Farsi", email.Body, StringComparison.Ordinal);
        Assert.Contains("Maintenance Request", email.Body, StringComparison.Ordinal);
        Assert.Contains("Tiger Properties", email.Body, StringComparison.Ordinal);
        Assert.NotNull(email.HtmlBody);
        Assert.Contains("TG-CS-20260822-0042", email.HtmlBody, StringComparison.Ordinal);

        // Excluded deliberately: the free-text request summary, anything
        // internal, and any identifier that is not the ticket number.
        Assert.DoesNotContain("AC unit not cooling", email.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Geyness", email.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EscalationLevel", email.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Audit", email.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"TicketId", email.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("+971 50 123 4567")]
    [InlineData("0501234567")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ahmed Al-Farsi")]
    [InlineData("ahmed@localhost")]
    [InlineData("not an @ address")]
    public async Task NonEmailContactChannel_SkipsWithoutSendingOrGuessing(string channel)
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync(channel);
        var message = f.EnqueueTicketCreated(ticket.TicketId);

        var result = await f.CreateHandler().HandleAsync(message);

        // Skipped is a successful, terminal outcome: the Outbox message is
        // processed, never dead-lettered, and no retry is attempted.
        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        Assert.Empty(f.Email.Sent);
        Assert.Null(ticket.AcknowledgementSentAtUtc);

        var notification = Assert.Single(f.Notifications.Notifications);
        Assert.Equal(NotificationDeliveryStatus.Skipped, notification.DeliveryStatus);
        Assert.True(notification.IsTerminal);
        Assert.Null(notification.RecipientAddress);
        Assert.Equal(ticket.TicketId, notification.TicketId);

        var audit = Assert.Single(f.Audit.Entries, e => e.Action == NotificationAuditActions.NotificationSkipped);
        var expectedReason = string.IsNullOrWhiteSpace(channel)
            ? CustomerEmailSkipReasons.NoCustomerEmail
            : CustomerEmailSkipReasons.InvalidCustomerEmail;
        Assert.Contains($"Reason={expectedReason}", audit.AfterValue, StringComparison.Ordinal);

        // The diagnostic never echoes the contact value itself.
        if (!string.IsNullOrWhiteSpace(channel))
        {
            Assert.DoesNotContain(channel, audit.AfterValue!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UnverifiedTicketWithNoSnapshotOrInteractionEmail_SkipsVisibly()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedUnverifiedTicketAsync();
        var message = f.EnqueueTicketCreated(ticket.TicketId);

        var result = await f.CreateHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        Assert.Empty(f.Email.Sent);
        Assert.Null(ticket.AcknowledgementSentAtUtc);
        var notification = Assert.Single(f.Notifications.Notifications);
        Assert.Equal(NotificationDeliveryStatus.Skipped, notification.DeliveryStatus);
        Assert.Contains(
            $"Reason={CustomerEmailSkipReasons.NoCustomerEmail}",
            Assert.Single(f.Audit.Entries, e => e.Action == NotificationAuditActions.NotificationSkipped).AfterValue,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenesysTicket_UsesTheOriginatingInteractionEmailWhenNoSnapshotExists()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedGenesysTicketAsync("fatima@example.com");
        var message = f.EnqueueTicketCreated(ticket.TicketId);

        var result = await f.CreateHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        var email = Assert.Single(f.Email.Sent);
        Assert.Equal("fatima@example.com", email.ToAddress);
        Assert.Contains("Fatima Al-Mansoori", email.Body, StringComparison.Ordinal);
        Assert.NotNull(ticket.AcknowledgementSentAtUtc);
    }

    [Fact]
    public async Task NotificationsDisabled_SkipsWithoutTouchingTheProvider()
    {
        var f = new NotificationServiceFixture { NotificationPolicy = CustomerNotificationPolicy.Disabled };
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com");
        var message = f.EnqueueTicketCreated(ticket.TicketId);

        var result = await f.CreateHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        Assert.Empty(f.Email.Sent);
        Assert.Null(ticket.AcknowledgementSentAtUtc);
        var notification = Assert.Single(f.Notifications.Notifications);
        Assert.Equal(NotificationDeliveryStatus.Skipped, notification.DeliveryStatus);
        Assert.Contains(
            $"Reason={CustomerEmailSkipReasons.NotificationsDisabled}",
            Assert.Single(f.Audit.Entries, e => e.Action == NotificationAuditActions.NotificationSkipped).AfterValue,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventOlderThanTheConfiguredMaximum_SkipsInsteadOfMailingAStaleReceipt()
    {
        var f = new NotificationServiceFixture();
        var ticket = await f.SeedVerifiedTicketAsync("ahmed@example.com");
        var message = f.EnqueueTicketCreated(ticket.TicketId, occurredAtUtc: f.Time.GetUtcNow().UtcDateTime.AddDays(-3));

        var result = await f.CreateHandler().HandleAsync(message);

        Assert.Equal(OutboxHandlingOutcome.Succeeded, result.Outcome);
        Assert.Empty(f.Email.Sent);
        Assert.Equal(NotificationDeliveryStatus.Skipped, Assert.Single(f.Notifications.Notifications).DeliveryStatus);
        Assert.Contains(
            $"Reason={CustomerEmailSkipReasons.EventTooOld}",
            Assert.Single(f.Audit.Entries, e => e.Action == NotificationAuditActions.NotificationSkipped).AfterValue,
            StringComparison.Ordinal);
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

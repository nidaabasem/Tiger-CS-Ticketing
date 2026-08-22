using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.BackgroundJobs;
using TigerCS.Integrations.Modules.EmailIntegration;

namespace TigerCS.Tests.Notifications.Domain;

/// <summary>Domain-level invariants of the Outbox/Notification pair, and the two guards that keep the recording adapter and its retry policy honest.</summary>
public class OutboxAndNotificationDomainTests
{
    private static readonly DateTime Now = new(2026, 8, 22, 9, 0, 0, DateTimeKind.Utc);

    private static OutboxMessage NewMessage(DateTime? occurredAtUtc = null) => new(
        Guid.NewGuid(),
        OutboxEventTypes.TicketCreated,
        "{}",
        Guid.NewGuid(),
        new IdempotencyRecord("Ticket:1:TicketCreated:v1", IdempotencyScopes.OutboxDispatch, occurredAtUtc ?? Now),
        occurredAtUtc ?? Now);

    [Fact]
    public void NewMessage_StartsPendingWithNoAttempts()
    {
        var message = NewMessage();

        Assert.Equal(OutboxMessageStatus.Pending, message.Status);
        Assert.Equal(0, message.Attempts);
        Assert.Null(message.ProcessedAtUtc);
        Assert.Null(message.LastError);
    }

    /// <summary>The status enum has no "InProgress" value (MVP-Data-Dictionary.md §2.23), so a terminal message must refuse a further claim outright.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TerminalMessage_RefusesAFurtherAttempt(bool processed)
    {
        var message = NewMessage();
        message.BeginAttempt();

        if (processed)
        {
            message.MarkProcessed(Now);
        }
        else
        {
            message.DeadLetter("done", Now);
        }

        Assert.Throws<InvalidOperationException>(message.BeginAttempt);
    }

    [Fact]
    public void TransientFailure_DeadLettersExactlyWhenTheAttemptBudgetIsSpent()
    {
        var message = NewMessage();

        message.BeginAttempt();
        Assert.True(message.RecordTransientFailure("boom", maxAttempts: 3, Now));
        Assert.Equal(OutboxMessageStatus.Pending, message.Status);

        message.BeginAttempt();
        Assert.True(message.RecordTransientFailure("boom", maxAttempts: 3, Now));
        Assert.Equal(OutboxMessageStatus.Pending, message.Status);

        message.BeginAttempt();
        Assert.False(message.RecordTransientFailure("boom", maxAttempts: 3, Now));
        Assert.Equal(OutboxMessageStatus.DeadLettered, message.Status);
    }

    /// <summary>LastError is nvarchar(2000) (§2.23); a provider exception can exceed that and must not overflow the column.</summary>
    [Fact]
    public void OverlongError_IsTruncatedToTheColumnWidth()
    {
        var message = NewMessage();
        message.BeginAttempt();

        message.DeadLetter(new string('x', 5000), Now);

        Assert.Equal(2000, message.LastError!.Length);
    }

    /// <summary>Backoff is derived from OccurredAtUtc and Attempts alone (no stored NextAttemptAtUtc column), and the gaps must widen.</summary>
    [Fact]
    public void Backoff_IsZeroOnTheFirstAttemptAndWidensThereafter()
    {
        var baseDelay = TimeSpan.FromSeconds(30);

        Assert.Equal(Now, OutboxMessage.NextEligibleAtUtc(Now, 0, baseDelay));
        Assert.Equal(Now.AddSeconds(30), OutboxMessage.NextEligibleAtUtc(Now, 1, baseDelay));
        Assert.Equal(Now.AddSeconds(90), OutboxMessage.NextEligibleAtUtc(Now, 2, baseDelay));
        Assert.Equal(Now.AddSeconds(210), OutboxMessage.NextEligibleAtUtc(Now, 3, baseDelay));
        Assert.Equal(Now.AddSeconds(450), OutboxMessage.NextEligibleAtUtc(Now, 4, baseDelay));
    }

    /// <summary>An absurd attempt count must not overflow into a negative offset, which would make the message immediately eligible and defeat the backoff entirely.</summary>
    [Fact]
    public void Backoff_DoesNotGoBackwardsForAnAbsurdAttemptCount()
    {
        var eligible = OutboxMessage.NextEligibleAtUtc(Now, 1_000_000, TimeSpan.FromSeconds(30));

        Assert.True(eligible > Now);
    }

    /// <summary>ADR-0014's key shape, composed in one place so a producer and a dedup check cannot spell it differently.</summary>
    [Fact]
    public void IdempotencyKey_IsTicketPlusEventTypePlusVersion()
    {
        Assert.Equal(
            "Ticket:42:TicketCreated:v1",
            OutboxEventTypes.IdempotencyKeyFor(OutboxEventTypes.TicketCreated, 42, 1));

        Assert.NotEqual(
            OutboxEventTypes.IdempotencyKeyFor(OutboxEventTypes.TicketCreated, 42, 1),
            OutboxEventTypes.IdempotencyKeyFor(OutboxEventTypes.TicketCreated, 43, 1));

        // A version bump makes a re-issued event a distinct logical event
        // rather than a suppressed duplicate.
        Assert.NotEqual(
            OutboxEventTypes.IdempotencyKeyFor(OutboxEventTypes.TicketCreated, 42, 1),
            OutboxEventTypes.IdempotencyKeyFor(OutboxEventTypes.TicketCreated, 42, 2));
    }

    [Fact]
    public void SentNotification_RefusesASecondSend()
    {
        var notification = new Notification(
            1, NotificationType.Acknowledgement, null, "a@example.com",
            NotificationChannel.Email, Guid.NewGuid(), Guid.NewGuid(), Now);

        notification.MarkSent();

        Assert.True(notification.IsTerminal);
        Assert.Throws<InvalidOperationException>(notification.MarkSent);
    }

    /// <summary>
    /// FR-SLA-05/ISSUE-019 at the domain level: recording the automated
    /// acknowledgement writes one field and touches nothing the SLA engine
    /// reads.
    /// </summary>
    [Fact]
    public void RecordAcknowledgementSent_TouchesNothingTheSlaEngineReads()
    {
        var ticket = Ticket.CreateVerified("TG-CS-20260822-0001", 1, 1, 1, 1, 2, "summary", Now);

        ticket.RecordAcknowledgementSent(Now.AddSeconds(3));

        Assert.Equal(Now.AddSeconds(3), ticket.AcknowledgementSentAtUtc);
        Assert.Null(ticket.FirstHumanResponseAtUtc);
        Assert.Equal(SlaState.Running, ticket.SlaState);
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
        Assert.Equal(EscalationLevel.None, ticket.EscalationLevel);
    }

    /// <summary>Write-once — the domain-level guard against a redelivered Outbox message emailing the same customer twice.</summary>
    [Fact]
    public void RecordAcknowledgementSent_IsWriteOnce()
    {
        var ticket = Ticket.CreateVerified("TG-CS-20260822-0001", 1, 1, 1, 1, 2, "summary", Now);
        ticket.RecordAcknowledgementSent(Now);

        var ex = Assert.Throws<AcknowledgementAlreadySentException>(() => ticket.RecordAcknowledgementSent(Now.AddMinutes(5)));
        Assert.Equal(Now, ex.SentAtUtc);
        Assert.Equal(Now, ticket.AcknowledgementSentAtUtc);
    }

    /// <summary>
    /// Recording the acknowledgement and recording a first human response are
    /// independent: neither reads or writes the other's field, in either
    /// order.
    /// </summary>
    [Fact]
    public void AcknowledgementAndFirstResponse_AreIndependentInBothOrders()
    {
        var a = Ticket.CreateVerified("TG-CS-20260822-0001", 1, 1, 1, 1, 2, "summary", Now);
        a.RecordAcknowledgementSent(Now.AddSeconds(2));
        a.RecordFirstHumanResponse(Now.AddMinutes(20));
        Assert.Equal(Now.AddSeconds(2), a.AcknowledgementSentAtUtc);
        Assert.Equal(Now.AddMinutes(20), a.FirstHumanResponseAtUtc);

        var b = Ticket.CreateVerified("TG-CS-20260822-0002", 1, 1, 1, 1, 2, "summary", Now);
        b.RecordFirstHumanResponse(Now.AddMinutes(20));
        b.RecordAcknowledgementSent(Now.AddMinutes(25));
        Assert.Equal(Now.AddMinutes(20), b.FirstHumanResponseAtUtc);
        Assert.Equal(Now.AddMinutes(25), b.AcknowledgementSentAtUtc);
    }

    /// <summary>
    /// A ticket created and closed inside the dispatcher's polling window must
    /// still be able to record what was already sent — refusing would lose the
    /// record of a delivery that genuinely happened, which is worse than
    /// recording it. Deliberately unlike every other mutating method on
    /// Ticket, and stated as such on the method itself.
    /// </summary>
    [Fact]
    public void RecordAcknowledgementSent_IsAllowedOnAClosedTicket()
    {
        var ticket = Ticket.CreateVerified("TG-CS-20260822-0001", 1, 1, 1, 1, 2, "summary", Now);
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, null);
        ticket.Close();

        ticket.RecordAcknowledgementSent(Now.AddMinutes(1));

        Assert.NotNull(ticket.AcknowledgementSentAtUtc);
    }

    /// <summary>The environment guard is conditional on the selected adapter, never on the environment name alone — a real adapter must be able to run in Production.</summary>
    [Theory]
    [InlineData("Recording", "Development", false)]
    [InlineData("Recording", "Testing", false)]
    [InlineData("Recording", "Production", true)]
    [InlineData("Recording", "Staging", true)]
    [InlineData("recording", "Production", true)]
    [InlineData("Office365EmailSender", "Production", false)]
    [InlineData("SmtpRelay", "Staging", false)]
    public void EmailSenderSafety_FlagsOnlyTheRecordingAdapterOutsideDevelopmentAndTesting(
        string provider, string environment, bool expectedUnsafe)
    {
        Assert.Equal(expectedUnsafe, EmailSenderSafety.IsUnsafe(provider, environment));
    }

    /// <summary>A misconfigured policy must not be able to disable the safety properties — an unbounded attempt count would defeat FR-NOT-05's dead-letter path entirely.</summary>
    [Fact]
    public void OutboxDispatchOptions_ClampAMisconfiguredPolicy()
    {
        var clamped = new OutboxDispatchOptions
        {
            MaxAttempts = 0,
            BatchSize = -5,
            BaseRetryDelay = TimeSpan.FromSeconds(-1)
        }.ToPolicy();

        Assert.Equal(1, clamped.MaxAttempts);
        Assert.Equal(1, clamped.BatchSize);
        Assert.Equal(OutboxDispatchPolicy.Default.BaseRetryDelay, clamped.BaseRetryDelay);

        var capped = new OutboxDispatchOptions { MaxAttempts = 10_000, BatchSize = 10_000 }.ToPolicy();
        Assert.Equal(20, capped.MaxAttempts);
        Assert.Equal(500, capped.BatchSize);
    }

    /// <summary>The recording adapter is bounded, so a long development session cannot grow it without limit.</summary>
    [Fact]
    public async Task RecordingEmailSender_BoundsWhatItKeeps()
    {
        var sender = new RecordingEmailSender(Microsoft.Extensions.Logging.Abstractions.NullLogger<RecordingEmailSender>.Instance);

        for (var i = 0; i < RecordingEmailSender.MaxRecordedDeliveries + 25; i++)
        {
            await sender.SendAsync(new Application.Modules.Notifications.Abstractions.EmailMessage(
                $"r{i}@example.com", "subject", "body", Guid.NewGuid()));
        }

        Assert.Equal(RecordingEmailSender.MaxRecordedDeliveries, sender.Recorded.Count);
    }

    /// <summary>Content-level: only approved fields, and the two the documents do not authorise are absent.</summary>
    [Fact]
    public void AcknowledgementBody_OmitsTheUnapprovedFields()
    {
        var body = TicketAcknowledgementContent.Body(
            "TG-CS-20260822-0001", Now, "Facilities Management", Now.AddHours(4));

        Assert.Contains("TG-CS-20260822-0001", body, StringComparison.Ordinal);
        Assert.Contains("Facilities Management", body, StringComparison.Ordinal);
        Assert.Contains("2026-08-22 09:00 UTC", body, StringComparison.Ordinal);
        Assert.Contains("2026-08-22 13:00 UTC", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Geyness", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A provisional ticket's SLA clock has not started (FR-TKT-09), so the line is omitted rather than filled with an invented time.</summary>
    [Fact]
    public void AcknowledgementBody_OmitsTheResponseTimeWhenNoSlaPeriodIsOpen()
    {
        var body = TicketAcknowledgementContent.Body("TG-FM-20260822-0001", Now, "Facilities Management", null);

        Assert.DoesNotContain("Expected first response", body, StringComparison.Ordinal);
        Assert.Contains("TG-FM-20260822-0001", body, StringComparison.Ordinal);
    }
}

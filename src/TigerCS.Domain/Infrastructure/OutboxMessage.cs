namespace TigerCS.Domain.Infrastructure;

/// <summary>
/// MVP-Data-Dictionary.md §2.23 / MVP-ERD.md §2.23 / ADR-0013 — the
/// transactional Outbox row. Written in the <i>same database transaction</i>
/// as the business state change that produced it, so the state change and
/// the intent to notify are atomic and the dual-write problem cannot occur
/// (NFR-REL-01).
///
/// <para>
/// <b>Owned by the Infrastructure module</b> (Module-Design.md — "Owned
/// data: <c>OutboxMessage</c>"), not by Notifications and not by Ticketing.
/// Ticketing writes one through <c>IOutboxWriter</c>, which is one of
/// Infrastructure's four public interfaces; Notifications consumes it. That
/// keeps Module-Design.md's prohibition intact in both directions —
/// Ticketing never references Notifications, and Infrastructure never
/// references a business-capability module.
/// </para>
///
/// <para>
/// <b>No payload PII.</b> The payload carries identifiers and non-personal
/// metadata only; a handler re-reads whatever customer data it needs from
/// the database at dispatch time. Security-Architecture.md §11 requires
/// contact details be masked or omitted from logs, and an Outbox row is
/// long-lived, operator-visible and reproduced into Hangfire's dashboard —
/// so the same discipline applies to it, and keeping the payload
/// identifier-only means no acknowledgement body, caller number or contact
/// name is ever persisted here.
/// </para>
/// </summary>
public class OutboxMessage
{
    public Guid OutboxMessageId { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    /// <summary>JSON (MVP-Data-Dictionary.md §2.23). Identifiers and non-personal metadata only — see this type's remarks.</summary>
    public string Payload { get; private set; } = string.Empty;

    public Guid CorrelationId { get; private set; }

    /// <summary>
    /// FK → <see cref="IdempotencyRecord"/>, required (MVP-ERD.md §2.23
    /// "many:1, Required, Restrict"). Replaces the bare <c>IdempotencyKey</c>
    /// string column of the superseded Architecture-Design.md sketch, so the
    /// one mechanism also covers Genesys webhook dedup and the SLA
    /// sweep/scheduled-job overlap (ADR-0014).
    /// </summary>
    public long IdempotencyRecordId { get; private set; }

    public OutboxMessageStatus Status { get; private set; }

    /// <summary>
    /// Incremented once per dispatch attempt, <i>before</i> the attempt runs.
    ///
    /// <para>
    /// <b>This is also the concurrency token</b> that stops two workers from
    /// delivering the same message (see this type's
    /// <see cref="BeginAttempt"/>). MVP-Data-Dictionary.md §2.23's
    /// <c>Status</c> enum has no "InProgress" value, so a claim cannot be
    /// modelled as a status transition without inventing one; a
    /// compare-and-swap on this already-approved column achieves the same
    /// exclusion with no schema invention.
    /// </para>
    /// </summary>
    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    public DateTime OccurredAtUtc { get; private set; }

    public DateTime? ProcessedAtUtc { get; private set; }

    private OutboxMessage() { }

    public OutboxMessage(
        Guid outboxMessageId,
        string eventType,
        string payload,
        Guid correlationId,
        long idempotencyRecordId,
        DateTime occurredAtUtc)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new ArgumentException("EventType is required.", nameof(eventType));
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new ArgumentException("Payload is required.", nameof(payload));
        }

        OutboxMessageId = outboxMessageId;
        EventType = eventType;
        Payload = payload;
        CorrelationId = correlationId;
        IdempotencyRecordId = idempotencyRecordId;
        Status = OutboxMessageStatus.Pending;
        Attempts = 0;
        OccurredAtUtc = occurredAtUtc;
    }

    /// <summary>
    /// Claims this message for one dispatch attempt by incrementing
    /// <see cref="Attempts"/>.
    ///
    /// <para>
    /// <b>Why an increment is a claim.</b> <see cref="Attempts"/> is mapped
    /// as an optimistic-concurrency token, so persisting this change emits
    /// <c>UPDATE OutboxMessages SET Attempts = @new WHERE OutboxMessageId =
    /// @id AND Attempts = @original</c>. Two workers that both read the same
    /// pending row therefore race on one atomic conditional update: exactly
    /// one affects a row, and the loser's save throws, which the dispatcher
    /// treats as "somebody else has this one" and skips. Without it, two
    /// Hangfire workers (or one worker and a manual re-run) would each send
    /// the same acknowledgement.
    /// </para>
    /// </summary>
    public void BeginAttempt()
    {
        if (Status != OutboxMessageStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Outbox message {OutboxMessageId} is {Status} and cannot be dispatched again.");
        }

        Attempts++;
    }

    /// <summary>Terminal success. <see cref="LastError"/> is deliberately preserved — the record of a transient failure that later succeeded is diagnostically useful and nothing in §2.23 says to clear it.</summary>
    public void MarkProcessed(DateTime processedAtUtc)
    {
        Status = OutboxMessageStatus.Processed;
        ProcessedAtUtc = processedAtUtc;
    }

    /// <summary>
    /// A failure that may succeed on a later attempt. The message stays
    /// <see cref="OutboxMessageStatus.Pending"/> and remains eligible for
    /// the dispatcher's next pass, unless this attempt exhausted
    /// <paramref name="maxAttempts"/> — in which case it dead-letters here
    /// rather than retrying forever (FR-NOT-05, MVP-API-Contracts.md §6.1).
    /// </summary>
    /// <returns><c>true</c> when a further attempt remains, <c>false</c> when this call dead-lettered the message.</returns>
    public bool RecordTransientFailure(string error, int maxAttempts, DateTime failedAtUtc)
    {
        LastError = Truncate(error);

        if (Attempts >= maxAttempts)
        {
            DeadLetter(error, failedAtUtc);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Terminal failure — retried out, or a failure no retry could fix (a
    /// recipient that cannot be resolved). The row is <b>kept</b>, never
    /// deleted: FR-NOT-05 requires a failed send stay visible in an
    /// operational queue with a dead-letter path, and ADR-0013's whole point
    /// is that nothing is lost.
    /// </summary>
    public void DeadLetter(string error, DateTime deadLetteredAtUtc)
    {
        Status = OutboxMessageStatus.DeadLettered;
        LastError = Truncate(error);
        ProcessedAtUtc = deadLetteredAtUtc;
    }

    /// <summary>
    /// The earliest moment this message may be attempted again, given how
    /// many attempts it has already had.
    ///
    /// <para>
    /// <b>Derived, not stored.</b> System-Architecture.md requires "retry
    /// with backoff", but MVP-Data-Dictionary.md §2.23 has no
    /// <c>NextAttemptAtUtc</c> column and this increment does not invent
    /// one. The schedule is instead a pure function of two approved columns
    /// — <see cref="OccurredAtUtc"/> and <see cref="Attempts"/> — which
    /// makes it deterministic, queryable, and identical however many
    /// processes compute it.
    /// </para>
    /// </summary>
    public static DateTime NextEligibleAtUtc(DateTime occurredAtUtc, int attempts, TimeSpan baseDelay) =>
        occurredAtUtc + CumulativeBackoff(attempts, baseDelay);

    /// <summary>
    /// Sum of an exponentially doubling delay over <paramref name="attempts"/>
    /// completed attempts: <c>base·(2^attempts − 1)</c>. With a 30-second
    /// base that is 0s / 30s / 90s / 210s / 450s before attempts 1–5 —
    /// increasing gaps, and the whole schedule bounded because
    /// <c>Attempts</c> itself is bounded by the dead-letter threshold.
    /// </summary>
    private static TimeSpan CumulativeBackoff(int attempts, TimeSpan baseDelay)
    {
        if (attempts <= 0)
        {
            return TimeSpan.Zero;
        }

        // Clamped so a corrupt or unexpectedly large Attempts value cannot
        // overflow the multiplication into a negative (i.e. immediately
        // eligible) offset — the failure direction that would defeat the
        // backoff entirely.
        var exponent = Math.Min(attempts, 16);
        return baseDelay * ((1L << exponent) - 1);
    }

    /// <summary>LastError is nvarchar(2000) (MVP-Data-Dictionary.md §2.23); a provider exception message can exceed that.</summary>
    private static string Truncate(string error) =>
        error.Length <= 2000 ? error : error[..2000];
}

/// <summary>MVP-Data-Dictionary.md §2.23: "Pending/Processed/DeadLettered". No "Failed" value exists — a retryable failure stays <see cref="Pending"/> with <c>Attempts</c>/<c>LastError</c> recorded.</summary>
public enum OutboxMessageStatus : byte
{
    Pending = 1,
    Processed = 2,
    DeadLettered = 3
}

/// <summary>
/// The <c>EventType</c> values MVP-API-Contracts.md names, spelled once so no
/// producer and consumer can disagree on the string.
/// </summary>
public static class OutboxEventTypes
{
    /// <summary>MVP-API-Contracts.md §3.1 and §2.6 — "drives the automated acknowledgement notification".</summary>
    public const string TicketCreated = "TicketCreated";

    /// <summary>
    /// The <c>EventVersion</c> component of ADR-0014's outbox idempotency key
    /// (<c>TicketId + EventType + EventVersion</c>). Bumping it for a given
    /// event type deliberately makes a re-issued event a distinct logical
    /// event rather than a suppressed duplicate.
    /// </summary>
    public const int TicketCreatedVersion = 1;

    /// <summary>ADR-0014's key shape, composed in one place so producer and dedup check cannot drift apart.</summary>
    public static string IdempotencyKeyFor(string eventType, long ticketId, int eventVersion) =>
        $"Ticket:{ticketId}:{eventType}:v{eventVersion}";
}

namespace TigerCS.Domain.Infrastructure;

/// <summary>
/// MVP-Data-Dictionary.md §2.23 / MVP-ERD.md §2.23 / ADR-0014 — the
/// generalized idempotency record, deliberately its own table rather than a
/// bare column on one consumer, so the same mechanism covers the Outbox
/// dispatcher, Genesys webhook dedup, and the SLA breach check.
///
/// <para>
/// <b>What this increment uses it for.</b> SLA-Architecture.md §15: "Every
/// breach/warning/escalation check carries an idempotency key
/// (<c>TicketId + CheckType + DueTimestamp</c>) so the scheduled job and the
/// sweep never produce a duplicate notification for the same due event."
/// Both detection paths of §13/§14 target the same key, so whichever arrives
/// second is a no-op. The unique index on
/// <see cref="IdempotencyKey"/> is what makes that true under genuine
/// concurrency rather than only under sequential retries — a lost race
/// surfaces as a unique-constraint violation, which the caller treats as
/// "already processed".
/// </para>
///
/// <para>
/// <b><see cref="ExpiresAtUtc"/> is never set by this increment.</b>
/// §2.23 marks the retention window <c>[ASSUMPTION — window not yet
/// specified]</c>, and purging breach-check records early would let a
/// re-delivered deadline check duplicate a breach. The column exists because
/// it is part of the approved shape; the housekeeping job that would consume
/// it is not in this increment's scope.
/// </para>
/// </summary>
public class IdempotencyRecord
{
    public long IdempotencyRecordId { get; private set; }

    /// <summary>Unique across the whole table (MVP-Data-Dictionary.md §2.23). Scope is part of the composed key's text, so two scopes cannot collide.</summary>
    public string IdempotencyKey { get; private set; } = string.Empty;

    /// <summary>"OutboxDispatch" / "GenesysWebhook" / "SlaBreachCheck" — see <see cref="IdempotencyScopes"/>.</summary>
    public string Scope { get; private set; } = string.Empty;

    public DateTime FirstSeenAtUtc { get; private set; }
    public DateTime LastSeenAtUtc { get; private set; }

    /// <summary>Lets a duplicate request return the same prior result rather than recomputing (MVP-Data-Dictionary.md §2.23).</summary>
    public string? ResultReference { get; private set; }

    /// <summary>Housekeeping only, and never populated here — see this type's remarks.</summary>
    public DateTime? ExpiresAtUtc { get; private set; }

    private IdempotencyRecord() { }

    public IdempotencyRecord(string idempotencyKey, string scope, DateTime firstSeenAtUtc, string? resultReference = null)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("IdempotencyKey is required.", nameof(idempotencyKey));
        }

        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("Scope is required.", nameof(scope));
        }

        IdempotencyKey = idempotencyKey;
        Scope = scope;
        FirstSeenAtUtc = firstSeenAtUtc;
        LastSeenAtUtc = firstSeenAtUtc;
        ResultReference = resultReference;
    }

    /// <summary>Records that the same key was seen again. <see cref="FirstSeenAtUtc"/> and <see cref="ResultReference"/> are never overwritten — the first outcome is the one a duplicate must observe.</summary>
    public void MarkSeenAgain(DateTime seenAtUtc) => LastSeenAtUtc = seenAtUtc;
}

/// <summary>The <c>Scope</c> values of MVP-Data-Dictionary.md §2.23, named once so no call site spells one differently.</summary>
public static class IdempotencyScopes
{
    /// <summary>The SLA breach check of SLA-Architecture.md §15.</summary>
    public const string SlaBreachCheck = "SlaBreachCheck";

    /// <summary>
    /// ADR-0013/ADR-0014's Outbox dedup. The key is composed at <i>enqueue</i>
    /// time from <c>TicketId + EventType + EventVersion</c>, inside the same
    /// transaction as the business state change, so the unique index makes
    /// "one logical event produces at most one Outbox message" a database
    /// fact rather than an application convention.
    /// </summary>
    public const string OutboxDispatch = "OutboxDispatch";
}

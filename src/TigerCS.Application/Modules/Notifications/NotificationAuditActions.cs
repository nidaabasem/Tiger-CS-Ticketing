namespace TigerCS.Application.Modules.Notifications;

/// <summary>
/// The <c>AuditEntries.Action</c> values this module writes (ADR-0018 §2 —
/// the generic table, since a notification delivery is not a change to one of
/// the five ticket-state dimensions and so does not belong in
/// <c>TicketStatusHistory</c>).
///
/// <para>
/// Together these five make FR-NOT-05's "all notification sends/failures are
/// logged, correlation-tracked" a property of the audit trail rather than of
/// log retention: every one carries the Outbox message's own correlation ID,
/// so a delivery can be traced end-to-end across the audit trail, the Outbox
/// and structured logs from one identifier (ADR-0014).
/// </para>
///
/// <para>
/// <b>No entry ever carries a message body, address or secret.</b>
/// <c>BeforeValue</c>/<c>AfterValue</c> hold status transitions, attempt
/// counts and identifiers only. Security-Architecture.md §11's masking rule
/// is written for logs; an audit row is longer-lived and more widely
/// readable, so it gets the stricter treatment — not the looser one.
/// </para>
/// </summary>
public static class NotificationAuditActions
{
    /// <summary>An Outbox message was written in the business transaction. The one entry produced synchronously, atomically with the state change it describes.</summary>
    public const string NotificationQueued = "NotificationQueued";

    public const string NotificationDeliverySucceeded = "NotificationDeliverySucceeded";

    public const string NotificationDeliveryFailed = "NotificationDeliveryFailed";

    /// <summary>A failed attempt that has budget left — records that another attempt is due, and when.</summary>
    public const string NotificationRetryScheduled = "NotificationRetryScheduled";

    /// <summary>Terminal. Retries exhausted, or a failure no retry could fix. The message and its notification row are retained, not discarded (FR-NOT-05).</summary>
    public const string NotificationDeadLettered = "NotificationDeadLettered";

    /// <summary><c>AuditEntries.EntityType</c> values used alongside the actions above.</summary>
    public const string OutboxMessageEntityType = "OutboxMessage";

    public const string NotificationEntityType = "Notification";
}

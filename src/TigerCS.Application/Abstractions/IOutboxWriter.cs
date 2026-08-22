using TigerCS.Domain.Infrastructure;

namespace TigerCS.Application.Abstractions;

/// <summary>
/// Module-Design.md names <c>IOutboxWriter</c> as one of the Infrastructure
/// module's four public interfaces. This is that port: the only supported way
/// for a business module to say "this happened, and something outside the
/// request must eventually observe it".
///
/// <para>
/// <b>Does not save.</b> Like <see cref="IAuditEntryWriter"/>, the
/// implementation adds rows to the caller's own unit of work and leaves the
/// commit to the caller — which is precisely what makes ADR-0013's guarantee
/// hold: the business state change and the Outbox row land in one
/// transaction, or neither lands. A caller that rolls back leaves no Outbox
/// message behind, so a rolled-back operation cannot notify anybody.
/// </para>
///
/// <para>
/// <b>Nothing is sent from here.</b> NFR-REL-01: nothing is dispatched from
/// application code inside a request handler. Delivery is the Hangfire
/// dispatcher's job (ADR-0015), after the transaction commits.
/// </para>
/// </summary>
public interface IOutboxWriter
{
    /// <summary>
    /// Enqueues one event, reserving its ADR-0014 idempotency key
    /// (<c>TicketId + EventType + EventVersion</c>) in the same unit of work.
    /// </summary>
    /// <returns>
    /// The new message, or <c>null</c> when the key is already reserved —
    /// meaning this logical event is already enqueued and a second Outbox row
    /// must not be written. A duplicate is a normal, expected outcome (a
    /// retried request), not an error.
    /// </returns>
    Task<OutboxMessage?> WriteAsync(
        string eventType,
        string payload,
        Guid correlationId,
        string idempotencyKey,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken = default);
}

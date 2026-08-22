using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Abstractions;
using TigerCS.Domain.Infrastructure;

namespace TigerCS.Infrastructure.Persistence;

/// <summary>
/// ADR-0013's Outbox writer — Module-Design.md's <c>IOutboxWriter</c>, the
/// Infrastructure module's public port for "this happened, and something
/// outside the request must eventually observe it".
///
/// <para>
/// <b>Adds, never saves.</b> Exactly like <c>AuditEntryWriter</c>: the rows
/// go into the caller's own <see cref="TigerCsDbContext"/> and the caller's
/// unit of work commits them alongside its business state change. That single
/// property is what makes ADR-0013's guarantee hold — the state change and
/// the intent to notify commit together or not at all, so a rolled-back
/// operation provably leaves no Outbox message behind and can never notify
/// anybody about something that did not happen.
/// </para>
///
/// <para>
/// <b>Reserves ADR-0014's idempotency key in the same breath.</b>
/// MVP-ERD.md §2.23 makes <c>IdempotencyRecordId</c> a required FK, so the
/// record is created here rather than at dispatch time. Because it commits
/// inside the caller's transaction and <c>IdempotencyKey</c> is uniquely
/// indexed, "one logical event produces at most one Outbox message" is a
/// database fact: two concurrent producers of the same event race on the
/// unique index and exactly one survives.
/// </para>
/// </summary>
public sealed class OutboxWriter(TigerCsDbContext dbContext) : IOutboxWriter
{
    public async Task<OutboxMessage?> WriteAsync(
        string eventType,
        string payload,
        Guid correlationId,
        string idempotencyKey,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("IdempotencyKey is required.", nameof(idempotencyKey));
        }

        // Change tracker first, then the database. Within one transaction the
        // same key may already have been reserved by an earlier call that has
        // not committed yet, and the database cannot see it.
        var pendingReservation = dbContext.ChangeTracker.Entries<IdempotencyRecord>()
            .Any(e => e.State == EntityState.Added && e.Entity.IdempotencyKey == idempotencyKey);

        if (pendingReservation)
        {
            return null;
        }

        var existing = await dbContext.IdempotencyRecords
            .FirstOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, cancellationToken);

        if (existing is not null)
        {
            // Already enqueued. Recording that the key was seen again keeps
            // the duplicate visible for diagnosis without producing a second
            // message.
            existing.MarkSeenAgain(occurredAtUtc);
            return null;
        }

        var outboxMessageId = Guid.NewGuid();

        var reservation = new IdempotencyRecord(
            idempotencyKey,
            IdempotencyScopes.OutboxDispatch,
            occurredAtUtc,
            // MVP-Data-Dictionary.md §2.23: "Lets a duplicate request return
            // the same prior result rather than recomputing" — here, which
            // message the first caller produced.
            resultReference: outboxMessageId.ToString());

        await dbContext.IdempotencyRecords.AddAsync(reservation, cancellationToken);

        // Both rows are Added in one unit of work, so EF orders the inserts
        // by their FK dependency and the record's database-generated id is
        // populated into the message before its own insert runs.
        // Passing the reservation itself, not its (still-unassigned) id: the
        // navigation is what lets EF order the two inserts and propagate the
        // generated key into this message's FK. See OutboxMessage's own
        // remarks.
        var message = new OutboxMessage(
            outboxMessageId, eventType, payload, correlationId, reservation, occurredAtUtc);

        await dbContext.OutboxMessages.AddAsync(message, cancellationToken);

        return message;
    }
}

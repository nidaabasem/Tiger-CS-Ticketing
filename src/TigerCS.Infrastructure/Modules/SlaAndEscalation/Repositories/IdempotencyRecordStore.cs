using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;
using TigerCS.Domain.Infrastructure;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.SlaAndEscalation.Repositories;

/// <summary>
/// ADR-0014's idempotency store over <c>IdempotencyRecords</c>.
///
/// <para>
/// <b>Two layers, deliberately.</b> The read below settles the ordinary case
/// — a scheduled job and a sweep arriving seconds apart — without an
/// exception. The unique index on <c>IdempotencyKey</c> settles the genuine
/// race, where both callers read "not present" before either commits: the
/// loser's SaveChanges fails on the constraint and its whole transaction
/// rolls back, so it writes no breach flag, no escalation row and no audit
/// entry. Neither layer alone is enough — the read alone is racy, and relying
/// on the constraint alone would turn every routine duplicate into a failed
/// transaction.
/// </para>
///
/// <para>
/// Adds to the caller's unit of work without saving, so the claim commits
/// atomically with the effects it guards. A claim that is never committed —
/// because the caller rolled back — is correctly not a claim at all.
/// </para>
/// </summary>
public sealed class IdempotencyRecordStore(TigerCsDbContext dbContext) : IIdempotencyRecordStore
{
    public async Task<bool> TryReserveAsync(
        string idempotencyKey, string scope, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        // Local first: within one transaction a caller may reserve the same
        // key twice (resolving a ticket evaluates both clocks, and the
        // scheduled job may already have added the row in this same unit of
        // work). ChangeTracker sees that; the database does not yet.
        var pending = dbContext.ChangeTracker.Entries<IdempotencyRecord>()
            .Any(e => e.State == EntityState.Added && e.Entity.IdempotencyKey == idempotencyKey);

        if (pending)
        {
            return false;
        }

        var existing = await dbContext.IdempotencyRecords
            .FirstOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, cancellationToken);

        if (existing is not null)
        {
            existing.MarkSeenAgain(nowUtc);
            return false;
        }

        await dbContext.IdempotencyRecords.AddAsync(new IdempotencyRecord(idempotencyKey, scope, nowUtc), cancellationToken);
        return true;
    }
}

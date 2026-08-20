using TigerCS.Application.Abstractions;
using TigerCS.Domain.Audit;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Audit;

public sealed class AuditEntryWriter(TigerCsDbContext dbContext, TimeProvider timeProvider) : IAuditEntryWriter
{
    public async Task WriteAsync(
        Guid? actorEmployeeId,
        string action,
        string entityType,
        string? entityId,
        string? beforeValue,
        string? afterValue,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        var entry = new AuditEntry(
            actorEmployeeId, action, entityType, entityId, beforeValue, afterValue, correlationId,
            timeProvider.GetUtcNow().UtcDateTime);

        // Not saved here — added to the same DbContext/transaction as the
        // caller's own state change, committed together by the caller's
        // unit-of-work SaveChangesAsync (mirrors the Outbox pattern's
        // transactional guarantee, ADR-0013).
        await dbContext.AuditEntries.AddAsync(entry, cancellationToken);
    }
}

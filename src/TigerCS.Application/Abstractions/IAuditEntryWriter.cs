namespace TigerCS.Application.Abstractions;

/// <summary>
/// Application-layer port over the append-only audit trail (ADR-0018,
/// MVP-Data-Dictionary.md §2.22). Implemented in Infrastructure; every
/// mutating action writes through this port, in the same transaction as the
/// state change it describes (mirrors the Outbox pattern's transactional
/// guarantee, ADR-0013), by not calling SaveChanges itself — the caller's own
/// unit-of-work commits both together.
/// </summary>
public interface IAuditEntryWriter
{
    Task WriteAsync(
        Guid? actorEmployeeId,
        string action,
        string entityType,
        string? entityId,
        string? beforeValue,
        string? afterValue,
        Guid correlationId,
        CancellationToken cancellationToken = default);
}

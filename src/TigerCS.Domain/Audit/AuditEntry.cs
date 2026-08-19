namespace TigerCS.Domain.Audit;

/// <summary>
/// Append-only audit trail row (ADR-0018, MVP-Data-Dictionary.md §2.22).
/// Deliberately no FK on <see cref="EntityId"/> — AuditEntries must be able
/// to record an action against any current or future entity type without a
/// schema change per new type (MVP-ERD.md §2.22).
///
/// This is a minimal, module-agnostic slice of the generalized Audit/Outbox
/// foundation (backlog S-05, not yet built) — added here because this
/// phase's task explicitly requires audit-ready events for CRM Verification.
/// It does not implement Outbox dispatch or idempotency; those remain S-05's
/// scope for later modules that need them.
/// </summary>
public class AuditEntry
{
    public long AuditEntryId { get; private set; }
    public Guid? ActorEmployeeId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string EntityType { get; private set; } = string.Empty;
    public string? EntityId { get; private set; }
    public string? BeforeValue { get; private set; }
    public string? AfterValue { get; private set; }
    public Guid CorrelationId { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }

    private AuditEntry() { }

    public AuditEntry(
        Guid? actorEmployeeId,
        string action,
        string entityType,
        string? entityId,
        string? beforeValue,
        string? afterValue,
        Guid correlationId,
        DateTime occurredAtUtc)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            throw new ArgumentException("Action is required.", nameof(action));
        }

        if (string.IsNullOrWhiteSpace(entityType))
        {
            throw new ArgumentException("EntityType is required.", nameof(entityType));
        }

        ActorEmployeeId = actorEmployeeId;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        BeforeValue = beforeValue;
        AfterValue = afterValue;
        CorrelationId = correlationId;
        OccurredAtUtc = occurredAtUtc;
    }
}

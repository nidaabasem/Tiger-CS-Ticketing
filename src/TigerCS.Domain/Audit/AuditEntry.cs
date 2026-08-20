namespace TigerCS.Domain.Audit;

/// <summary>
/// Append-only audit trail row (ADR-0018, MVP-Data-Dictionary.md §2.22).
/// Deliberately no FK on <see cref="EntityId"/> — AuditEntries must be able
/// to record an action against any current or future entity type without a
/// schema change per new type (MVP-ERD.md §2.22).
///
/// <para>
/// <b>Reconciliation with the approved ERD and the future S-05 module.</b>
/// This is not a CRM-owned, temporary audit schema that S-05 will later
/// replace — the columns below are the exact canonical shape from
/// MVP-Data-Dictionary.md §2.22 (table name, every column, and the
/// deliberate no-FK-on-EntityId design), not an approximation. Backlog
/// S-05 ("Audit trail, Outbox, and idempotency foundation") is three
/// things bundled as one backlog item: (1) an audit-writing mechanism used
/// by every later feature, (2) an Outbox dispatch loop, (3)
/// <c>IdempotencyRecords</c>. Only (1) is built here, using the real,
/// final table — writing an <see cref="AuditEntry"/> row through
/// <c>IAuditEntryWriter</c> in the same DB transaction as the state change
/// it describes, per ADR-0018, with no update/delete path at any privilege
/// level. (2) and (3) remain S-05's scope; when that work lands it extends
/// this table's consumers (e.g. Identity and Access's own login/logout
/// audit, deferred in PR #9), it does not migrate or rename it.
/// </para>
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

namespace TigerCS.Domain.Modules.CustomerVerification;

/// <summary>
/// A short-lived, pre-ticket record of an agent's CRM unit/contact lookup and
/// verbal read-back confirmation (MVP-ERD.md §2.24, Finding DR-01 — resolves
/// the circular dependency where ticket creation needed a confirmation that
/// itself needed a ticket to exist). Single-agent-owned, single-use,
/// write-once snapshot; a ticket is created *from* a confirmed, unconsumed
/// session in a later phase (Ticketing module, out of scope here).
///
/// For this pilot phase (MVP-Implementation-Backlog.md §0.2/S-07), the
/// application layer combines unit/contact selection and verbal confirmation
/// into a single call, rather than MVP-API-Contracts.md §2.4's separate
/// start/select/confirm endpoints — but this entity still models the full
/// state machine, so the single-use/expiry/ownership invariants are real,
/// not simulated, and ready for the Ticketing module to consume unchanged.
/// </summary>
public class VerificationSession
{
    public Guid VerificationSessionId { get; private set; }
    public Guid AgentEmployeeId { get; private set; }
    public int UnitReferenceId { get; private set; }
    public int ContactReferenceId { get; private set; }

    public string? SnapshotUnitNumber { get; private set; }
    public string? SnapshotPropertyName { get; private set; }
    public string? SnapshotTowerName { get; private set; }
    public string? SnapshotUnitType { get; private set; }
    public string? SnapshotContactDisplayName { get; private set; }
    public string? SnapshotContactChannel { get; private set; }

    public bool ConfirmedVerbally { get; private set; }
    public VerificationSessionStatus Status { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ConfirmedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? ConsumedAtUtc { get; private set; }
    public long? ConsumedByTicketId { get; private set; }

    /// <summary>
    /// Client-supplied idempotency key for the combined create+confirm call
    /// (S-07) — a pilot-scoped, module-local substitute for the generalized
    /// IdempotencyRecords mechanism (ADR-0014 / backlog S-05), which has not
    /// been built yet. Null when the caller didn't supply one.
    /// </summary>
    public string? IdempotencyKey { get; private set; }

    private VerificationSession() { }

    public VerificationSession(
        Guid verificationSessionId,
        Guid agentEmployeeId,
        int unitReferenceId,
        int contactReferenceId,
        string? snapshotUnitNumber,
        string? snapshotPropertyName,
        string? snapshotTowerName,
        string? snapshotUnitType,
        string? snapshotContactDisplayName,
        string? snapshotContactChannel,
        DateTime createdAtUtc,
        DateTime expiresAtUtc,
        string? idempotencyKey)
    {
        if (verificationSessionId == Guid.Empty)
        {
            throw new ArgumentException("VerificationSessionId is required.", nameof(verificationSessionId));
        }

        if (agentEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("AgentEmployeeId is required.", nameof(agentEmployeeId));
        }

        if (expiresAtUtc <= createdAtUtc)
        {
            throw new ArgumentException("ExpiresAtUtc must be after CreatedAtUtc.", nameof(expiresAtUtc));
        }

        VerificationSessionId = verificationSessionId;
        AgentEmployeeId = agentEmployeeId;
        UnitReferenceId = unitReferenceId;
        ContactReferenceId = contactReferenceId;
        SnapshotUnitNumber = snapshotUnitNumber;
        SnapshotPropertyName = snapshotPropertyName;
        SnapshotTowerName = snapshotTowerName;
        SnapshotUnitType = snapshotUnitType;
        SnapshotContactDisplayName = snapshotContactDisplayName;
        SnapshotContactChannel = snapshotContactChannel;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        IdempotencyKey = idempotencyKey;
        Status = VerificationSessionStatus.InProgress;
    }

    public bool IsOwnedBy(Guid employeeId) => AgentEmployeeId == employeeId;

    /// <summary>
    /// Reflects Expired even if a background expiry sweep hasn't run yet
    /// (MVP-ERD.md §2.24) — computed at read time, never persisted implicitly.
    /// Applies while InProgress or Confirmed-but-not-yet-consumed; a session
    /// already Consumed is a settled historical fact and is never
    /// retroactively treated as expired.
    /// </summary>
    public VerificationSessionStatus EffectiveStatus(DateTime nowUtc) =>
        Status is VerificationSessionStatus.InProgress or VerificationSessionStatus.Confirmed && nowUtc > ExpiresAtUtc
            ? VerificationSessionStatus.Expired
            : Status;

    /// <summary>Records the agent's verbal read-back confirmation (MVP-API-Contracts.md §2.4.3, folded into S-07's single call).</summary>
    public void ConfirmVerbally(DateTime confirmedAtUtc)
    {
        if (EffectiveStatus(confirmedAtUtc) == VerificationSessionStatus.Expired)
        {
            throw new VerificationSessionExpiredException(VerificationSessionId);
        }

        if (Status != VerificationSessionStatus.InProgress)
        {
            throw new VerificationSessionNotInProgressException(VerificationSessionId, Status);
        }

        ConfirmedVerbally = true;
        ConfirmedAtUtc = confirmedAtUtc;
        Status = VerificationSessionStatus.Confirmed;
    }

    /// <summary>
    /// Marks the session consumed by a ticket — single-use (MVP-ERD.md
    /// §2.24). Not called by any endpoint in this phase (ticket creation is
    /// out of scope, per MVP-Implementation-Backlog.md S-08); implemented and
    /// unit-tested now so the invariant is real and ready for the Ticketing
    /// module to call unchanged.
    /// </summary>
    public void Consume(long ticketId, DateTime consumedAtUtc)
    {
        if (Status == VerificationSessionStatus.Consumed)
        {
            throw new VerificationSessionAlreadyConsumedException(VerificationSessionId);
        }

        if (EffectiveStatus(consumedAtUtc) == VerificationSessionStatus.Expired)
        {
            throw new VerificationSessionExpiredException(VerificationSessionId);
        }

        if (Status != VerificationSessionStatus.Confirmed)
        {
            throw new VerificationSessionNotConfirmedException(VerificationSessionId);
        }

        Status = VerificationSessionStatus.Consumed;
        ConsumedAtUtc = consumedAtUtc;
        ConsumedByTicketId = ticketId;
    }

    /// <summary>Explicit sweep transition, mirroring the Hangfire sweep pattern (ADR-0015) other expiring entities use.</summary>
    public void MarkExpired(DateTime nowUtc)
    {
        if (Status == VerificationSessionStatus.InProgress && nowUtc > ExpiresAtUtc)
        {
            Status = VerificationSessionStatus.Expired;
        }
    }
}

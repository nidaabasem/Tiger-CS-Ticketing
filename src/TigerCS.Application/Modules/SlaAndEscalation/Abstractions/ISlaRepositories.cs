using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Application.Modules.SlaAndEscalation.Abstractions;

/// <summary>Reads the seeded per-priority SLA targets (MVP-ERD.md §2.6). Reference data — never written by this module.</summary>
public interface ISlaPolicyRepository
{
    Task<SlaPolicy?> GetByPriorityIdAsync(byte priorityId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Loads the active business calendar already resolved into the exact shape
/// <see cref="SlaDueDateCalculator"/> consumes, so the calculation itself
/// stays a pure function (ADR-0010).
/// </summary>
public interface IBusinessCalendarRepository
{
    /// <summary>The single active calendar plus its seven working-day rows and every holiday date, or null if none is seeded.</summary>
    Task<BusinessCalendarSnapshot?> GetActiveSnapshotAsync(CancellationToken cancellationToken = default);
}

public interface ITicketSlaInstanceRepository
{
    /// <summary>The ticket's current period — the one row with <c>PeriodEndAtUtc IS NULL</c> (MVP-ERD.md §2.15).</summary>
    Task<TicketSlaInstance?> GetCurrentAsync(long ticketId, CancellationToken cancellationToken = default);

    /// <summary>Full SLA history for a ticket, oldest period first. Never filtered — ISSUE-023 requires every period a ticket passed through to remain reportable.</summary>
    Task<IReadOnlyList<TicketSlaInstance>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default);

    Task AddAsync(TicketSlaInstance instance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Current periods whose <paramref name="deadlineType"/> deadline has
    /// fallen due at or before <paramref name="asOfUtc"/> and is not yet
    /// flagged breached — the candidate set for SLA-Architecture.md §14's
    /// safety sweep. Deliberately excludes tickets whose clock is not running
    /// and tickets already past the relevant achievement event, so the sweep
    /// reads a small set rather than every open ticket.
    /// </summary>
    Task<IReadOnlyList<long>> ListTicketIdsWithDeadlineDueAsync(
        SlaDeadlineType deadlineType, DateTime asOfUtc, int maxResults, CancellationToken cancellationToken = default);
}

public interface ITicketEscalationRepository
{
    Task AddAsync(TicketEscalation escalation, CancellationToken cancellationToken = default);

    /// <summary>Escalation history for a ticket, most recent first (MVP-API-Contracts.md §5.9).</summary>
    Task<IReadOnlyList<TicketEscalation>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether an <see cref="EscalationTriggerType.AutoBreach"/> row already
    /// exists for this ticket. Backs the "exactly one Level 2 escalation, not
    /// a repeating one on every subsequent check" rule (backlog S-11's own
    /// test requirement); the filtered unique index on the table is the
    /// concurrent-race backstop behind it.
    /// </summary>
    Task<bool> HasAutomaticBreachEscalationAsync(long ticketId, CancellationToken cancellationToken = default);
}

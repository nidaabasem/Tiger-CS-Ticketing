using TigerCS.Domain.Infrastructure;

namespace TigerCS.Application.Modules.SlaAndEscalation.Abstractions;

/// <summary>
/// ADR-0014's idempotency mechanism, over <c>IdempotencyRecords</c>
/// (MVP-Data-Dictionary.md §2.23).
///
/// <para>
/// Reserving a key is a claim, not a query: the caller proceeds only if the
/// claim succeeded. That is what lets SLA-Architecture.md §13's scheduled job
/// and §14's safety sweep both target the same due event without the second
/// one duplicating its effects.
/// </para>
/// </summary>
public interface IIdempotencyRecordStore
{
    /// <summary>
    /// Attempts to claim <paramref name="idempotencyKey"/> for
    /// <paramref name="scope"/>, adding the record to the caller's unit of
    /// work when the claim is new.
    /// </summary>
    /// <returns>
    /// <c>true</c> when this caller now owns the key and should do the work;
    /// <c>false</c> when it was already claimed — in which case the existing
    /// record's <c>LastSeenAtUtc</c> is updated so a repeat is observable.
    /// </returns>
    Task<bool> TryReserveAsync(string idempotencyKey, string scope, DateTime nowUtc, CancellationToken cancellationToken = default);
}

/// <summary>
/// Builds the breach-check idempotency key of SLA-Architecture.md §15 —
/// <c>TicketId + CheckType + DueTimestamp</c>.
///
/// <para>
/// The due timestamp is part of the key deliberately: if a later increment
/// recomputes a deadline (backlog S-14's priority upgrade moves both due
/// dates), the <i>new</i> deadline is a genuinely different due event and
/// must be checkable, while re-checking the old one stays suppressed. Round-
/// tripped through <c>"O"</c> so the key text is culture-invariant and
/// exact to the tick.
/// </para>
/// </summary>
public static class SlaBreachIdempotencyKey
{
    public static string For(long ticketId, Domain.Modules.SlaAndEscalation.SlaDeadlineType deadlineType, DateTime dueAtUtc) =>
        $"SlaBreach:{ticketId}:{deadlineType}:{dueAtUtc.ToUniversalTime():O}";
}

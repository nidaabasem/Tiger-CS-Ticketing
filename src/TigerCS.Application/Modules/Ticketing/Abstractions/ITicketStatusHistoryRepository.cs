using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface ITicketStatusHistoryRepository
{
    Task AddAsync(TicketStatusHistory entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recent transition of <paramref name="dimension"/> INTO
    /// <paramref name="newValue"/> for one ticket, or null when it never
    /// happened.
    ///
    /// <para>
    /// Exists because the approved Reopen rule measures its window from the
    /// moment a ticket was closed, and a Closed ticket carries no closure
    /// timestamp column of its own. Rather than inventing one — or
    /// substituting the resolution timestamp, which is a different moment —
    /// the window reads the lifecycle history <c>CloseAsync</c> has always
    /// written. "Most recent" matters: a ticket closed, reopened and closed
    /// again is measured from its latest closure, never its first.
    /// </para>
    /// </summary>
    Task<TicketStatusHistory?> GetLatestTransitionIntoAsync(
        long ticketId, TicketStatusDimension dimension, byte newValue, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="GetLatestTransitionIntoAsync"/> for many tickets in one
    /// query, keyed by TicketId — the list surfaces (customer history, the
    /// related-tickets panel) stamp reopen eligibility on every row, and must
    /// not do so one round trip at a time. Tickets with no such transition are
    /// simply absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<long, DateTime>> ListLatestTransitionMomentsAsync(
        IReadOnlyCollection<long> ticketIds,
        TicketStatusDimension dimension,
        byte newValue,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One ticket's full lifecycle history, oldest first. Append-only and
    /// never filtered — this is the record, not a summary of it.
    ///
    /// <para>
    /// The read half of ADR-0018's store, added so Ticket Details can finally
    /// show what the table has always held: until now nothing exposed it, so
    /// the Activity feed could show only notes and escalations, and the reopen
    /// reason agents were required to type was written and never seen. Serving
    /// the existing rows was the alternative to inventing a second activity
    /// store beside them.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<TicketStatusHistory>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default);
}

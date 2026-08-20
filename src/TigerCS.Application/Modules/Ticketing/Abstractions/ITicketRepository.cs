using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface ITicketRepository
{
    Task<Ticket?> GetByIdAsync(long ticketId, CancellationToken cancellationToken = default);

    Task AddAsync(Ticket ticket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Count of existing tickets whose TicketNumber already starts with
    /// <paramref name="ticketNumberPrefix"/> (e.g. "TG-CS-20260820-") — used
    /// to compute the next per-department-per-day sequence segment
    /// (FR-TKT-01). The unique index on TicketNumber remains the actual
    /// correctness backstop under concurrency (TicketingUnitOfWork retries
    /// on a collision); this count only picks a good first guess.
    /// </summary>
    Task<int> CountByTicketNumberPrefixAsync(string ticketNumberPrefix, CancellationToken cancellationToken = default);
}

using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface ITicketResolutionRepository
{
    Task<TicketResolution?> GetCurrentAsync(long ticketId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The current (<c>IsCurrent</c>) resolution for each of the given
    /// tickets, in one query — used to stamp reopen-eligibility onto a page
    /// of customer history without a per-row round trip. Tickets with no
    /// current resolution simply have no entry.
    /// </summary>
    Task<IReadOnlyDictionary<long, TicketResolution>> ListCurrentByTicketIdsAsync(
        IReadOnlyCollection<long> ticketIds, CancellationToken cancellationToken = default);

    Task AddAsync(TicketResolution resolution, CancellationToken cancellationToken = default);
}

using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface ITicketResolutionRepository
{
    Task<TicketResolution?> GetCurrentAsync(long ticketId, CancellationToken cancellationToken = default);

    Task AddAsync(TicketResolution resolution, CancellationToken cancellationToken = default);
}

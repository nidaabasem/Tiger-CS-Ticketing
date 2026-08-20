using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface ITicketStatusHistoryRepository
{
    Task AddAsync(TicketStatusHistory entry, CancellationToken cancellationToken = default);
}

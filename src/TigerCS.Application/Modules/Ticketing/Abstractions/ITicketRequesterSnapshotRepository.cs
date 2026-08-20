using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface ITicketRequesterSnapshotRepository
{
    Task AddAsync(TicketRequesterSnapshot snapshot, CancellationToken cancellationToken = default);
}

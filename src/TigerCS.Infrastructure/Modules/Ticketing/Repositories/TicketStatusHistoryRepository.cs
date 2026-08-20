using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class TicketStatusHistoryRepository(TigerCsDbContext dbContext) : ITicketStatusHistoryRepository
{
    public async Task AddAsync(TicketStatusHistory entry, CancellationToken cancellationToken = default) =>
        await dbContext.TicketStatusHistoryEntries.AddAsync(entry, cancellationToken);
}

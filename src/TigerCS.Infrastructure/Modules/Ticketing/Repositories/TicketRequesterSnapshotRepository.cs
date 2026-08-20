using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class TicketRequesterSnapshotRepository(TigerCsDbContext dbContext) : ITicketRequesterSnapshotRepository
{
    public async Task AddAsync(TicketRequesterSnapshot snapshot, CancellationToken cancellationToken = default) =>
        await dbContext.TicketRequesterSnapshots.AddAsync(snapshot, cancellationToken);
}

using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class TicketResolutionRepository(TigerCsDbContext dbContext) : ITicketResolutionRepository
{
    public Task<TicketResolution?> GetCurrentAsync(long ticketId, CancellationToken cancellationToken = default) =>
        dbContext.TicketResolutions.FirstOrDefaultAsync(r => r.TicketId == ticketId && r.IsCurrent, cancellationToken);

    public async Task<IReadOnlyDictionary<long, TicketResolution>> ListCurrentByTicketIdsAsync(
        IReadOnlyCollection<long> ticketIds, CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.TicketResolutions
            .Where(r => r.IsCurrent && ticketIds.Contains(r.TicketId))
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.TicketId);
    }

    public async Task AddAsync(TicketResolution resolution, CancellationToken cancellationToken = default) =>
        await dbContext.TicketResolutions.AddAsync(resolution, cancellationToken);
}

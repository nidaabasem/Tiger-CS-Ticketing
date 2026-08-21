using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class TicketResolutionRepository(TigerCsDbContext dbContext) : ITicketResolutionRepository
{
    public Task<TicketResolution?> GetCurrentAsync(long ticketId, CancellationToken cancellationToken = default) =>
        dbContext.TicketResolutions.FirstOrDefaultAsync(r => r.TicketId == ticketId && r.IsCurrent, cancellationToken);

    public async Task AddAsync(TicketResolution resolution, CancellationToken cancellationToken = default) =>
        await dbContext.TicketResolutions.AddAsync(resolution, cancellationToken);
}

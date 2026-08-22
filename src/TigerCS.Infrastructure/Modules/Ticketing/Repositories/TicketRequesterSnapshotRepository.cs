using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class TicketRequesterSnapshotRepository(TigerCsDbContext dbContext) : ITicketRequesterSnapshotRepository
{
    public async Task AddAsync(TicketRequesterSnapshot snapshot, CancellationToken cancellationToken = default) =>
        await dbContext.TicketRequesterSnapshots.AddAsync(snapshot, cancellationToken);

    /// <summary>
    /// No-tracking: the snapshot is write-once (ADR-0007) and this read exists
    /// only to resolve an acknowledgement recipient, so there is nothing to
    /// track and nothing a caller could legitimately save back.
    /// </summary>
    public async Task<TicketRequesterSnapshot?> GetByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        await dbContext.TicketRequesterSnapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TicketId == ticketId, cancellationToken);
}

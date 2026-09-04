using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class TicketPendingRecordRepository(TigerCsDbContext dbContext) : ITicketPendingRecordRepository
{
    public Task<TicketPendingRecord?> GetOpenAsync(long ticketId, CancellationToken cancellationToken = default) =>
        dbContext.TicketPendingRecords.FirstOrDefaultAsync(
            p => p.TicketId == ticketId && p.ResumedAtUtc == null, cancellationToken);

    public async Task<IReadOnlyList<TicketPendingRecord>> ListByTicketIdAsync(
        long ticketId, CancellationToken cancellationToken = default) =>
        await dbContext.TicketPendingRecords
            .Where(p => p.TicketId == ticketId)
            .OrderBy(p => p.StartedAtUtc)
            .ThenBy(p => p.TicketPendingRecordId)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(TicketPendingRecord record, CancellationToken cancellationToken = default) =>
        await dbContext.TicketPendingRecords.AddAsync(record, cancellationToken);
}

public sealed class TicketInteractionRepository(TigerCsDbContext dbContext) : ITicketInteractionRepository
{
    public Task<TicketInteraction?> GetOriginatingAsync(long ticketId, CancellationToken cancellationToken = default) =>
        dbContext.TicketInteractions.FirstOrDefaultAsync(
            i => i.TicketId == ticketId && i.IsOriginatingInteraction, cancellationToken);

    public async Task<IReadOnlyList<TicketInteraction>> ListByTicketIdAsync(
        long ticketId, CancellationToken cancellationToken = default) =>
        await dbContext.TicketInteractions
            .Where(i => i.TicketId == ticketId)
            .OrderBy(i => i.CreatedAtUtc)
            .ThenBy(i => i.TicketInteractionId)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(TicketInteraction interaction, CancellationToken cancellationToken = default) =>
        await dbContext.TicketInteractions.AddAsync(interaction, cancellationToken);
}

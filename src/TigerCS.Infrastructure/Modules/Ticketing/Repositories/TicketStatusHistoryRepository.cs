using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class TicketStatusHistoryRepository(TigerCsDbContext dbContext) : ITicketStatusHistoryRepository
{
    public async Task AddAsync(TicketStatusHistory entry, CancellationToken cancellationToken = default) =>
        await dbContext.TicketStatusHistoryEntries.AddAsync(entry, cancellationToken);

    public async Task<TicketStatusHistory?> GetLatestTransitionIntoAsync(
        long ticketId, TicketStatusDimension dimension, byte newValue, CancellationToken cancellationToken = default) =>
        await dbContext.TicketStatusHistoryEntries
            .Where(h => h.TicketId == ticketId && h.Dimension == dimension && h.NewValue == newValue)
            // Ties broken by the append-only identity, so a ticket closed
            // twice within one clock tick still reports its latest closure.
            .OrderByDescending(h => h.OccurredAtUtc).ThenByDescending(h => h.TicketStatusHistoryId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<long, DateTime>> ListLatestTransitionMomentsAsync(
        IReadOnlyCollection<long> ticketIds,
        TicketStatusDimension dimension,
        byte newValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticketIds);

        if (ticketIds.Count == 0)
        {
            return new Dictionary<long, DateTime>();
        }

        // Grouped in the database rather than materializing every historical
        // row: the caller wants one timestamp per ticket, not the history.
        var moments = await dbContext.TicketStatusHistoryEntries
            .Where(h => ticketIds.Contains(h.TicketId) && h.Dimension == dimension && h.NewValue == newValue)
            .GroupBy(h => h.TicketId)
            .Select(g => new { TicketId = g.Key, OccurredAtUtc = g.Max(h => h.OccurredAtUtc) })
            .ToListAsync(cancellationToken);

        return moments.ToDictionary(m => m.TicketId, m => m.OccurredAtUtc);
    }

    public async Task<IReadOnlyList<TicketStatusHistory>> ListByTicketIdAsync(
        long ticketId, CancellationToken cancellationToken = default) =>
        await dbContext.TicketStatusHistoryEntries
            .Where(h => h.TicketId == ticketId)
            .OrderBy(h => h.OccurredAtUtc).ThenBy(h => h.TicketStatusHistoryId)
            .ToListAsync(cancellationToken);
}

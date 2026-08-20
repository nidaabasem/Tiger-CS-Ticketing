using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class TicketRepository(TigerCsDbContext dbContext) : ITicketRepository
{
    public Task<Ticket?> GetByIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        dbContext.Tickets.FirstOrDefaultAsync(t => t.TicketId == ticketId, cancellationToken);

    public async Task AddAsync(Ticket ticket, CancellationToken cancellationToken = default) =>
        await dbContext.Tickets.AddAsync(ticket, cancellationToken);

    public Task<int> CountByTicketNumberPrefixAsync(string ticketNumberPrefix, CancellationToken cancellationToken = default) =>
        dbContext.Tickets.CountAsync(t => t.TicketNumber.StartsWith(ticketNumberPrefix), cancellationToken);
}

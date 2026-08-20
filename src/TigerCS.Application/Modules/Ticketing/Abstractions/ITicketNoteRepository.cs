using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface ITicketNoteRepository
{
    Task AddAsync(TicketNote note, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TicketNote>> ListByTicketIdAsync(
        long ticketId, int page, int pageSize, CancellationToken cancellationToken = default);

    Task<int> CountByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default);
}

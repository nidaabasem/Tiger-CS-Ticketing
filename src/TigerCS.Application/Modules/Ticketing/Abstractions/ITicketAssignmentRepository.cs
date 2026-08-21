using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface ITicketAssignmentRepository
{
    Task<TicketAssignment?> GetCurrentAsync(long ticketId, CancellationToken cancellationToken = default);

    Task AddAsync(TicketAssignment assignment, CancellationToken cancellationToken = default);
}

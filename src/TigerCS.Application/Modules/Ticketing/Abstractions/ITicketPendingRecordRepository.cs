using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>Structured pending periods (Workflow/Automation phase 2) — append-plus-resume, never deleted.</summary>
public interface ITicketPendingRecordRepository
{
    /// <summary>The ticket's open pending period (<c>ResumedAtUtc IS NULL</c>), or null when the ticket is not pending. At most one exists per ticket.</summary>
    Task<TicketPendingRecord?> GetOpenAsync(long ticketId, CancellationToken cancellationToken = default);

    /// <summary>Full pending history for a ticket, oldest first.</summary>
    Task<IReadOnlyList<TicketPendingRecord>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default);

    Task AddAsync(TicketPendingRecord record, CancellationToken cancellationToken = default);
}

/// <summary>The interaction context a ticket was created from (Workflow/Automation phase 2) — at most one row per ticket, write-once.</summary>
public interface ITicketInteractionContextRepository
{
    Task<TicketInteractionContext?> GetByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default);

    Task AddAsync(TicketInteractionContext context, CancellationToken cancellationToken = default);
}

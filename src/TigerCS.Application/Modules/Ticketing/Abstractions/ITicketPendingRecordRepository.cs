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

/// <summary>
/// A ticket's customer interactions (Workflow/Automation phase 2, hardened
/// to one-to-many pre-phase-3) — append-only; a ticket accumulates
/// interactions over its lifetime, exactly one of which is the originating
/// one.
/// </summary>
public interface ITicketInteractionRepository
{
    /// <summary>The interaction the ticket was created from, or null for tickets predating this model.</summary>
    Task<TicketInteraction?> GetOriginatingAsync(long ticketId, CancellationToken cancellationToken = default);

    /// <summary>All of a ticket's interactions, oldest first.</summary>
    Task<IReadOnlyList<TicketInteraction>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default);

    Task AddAsync(TicketInteraction interaction, CancellationToken cancellationToken = default);
}

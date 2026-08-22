using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface ITicketRequesterSnapshotRepository
{
    Task AddAsync(TicketRequesterSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// The ticket's immutable requester snapshot, or <c>null</c> for a
    /// provisional (ISSUE-006) ticket that has not been reconciled yet.
    ///
    /// <para>
    /// Added by the Notifications increment: resolving an acknowledgement
    /// recipient must read the verified snapshot and nothing else (ADR-0006/
    /// ADR-0007), which needs a read path this repository did not previously
    /// expose. Read-only by construction — <see cref="TicketRequesterSnapshot"/>
    /// has no mutating member, so handing one out cannot become a way to edit
    /// it.
    /// </para>
    /// </summary>
    Task<TicketRequesterSnapshot?> GetByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default);
}

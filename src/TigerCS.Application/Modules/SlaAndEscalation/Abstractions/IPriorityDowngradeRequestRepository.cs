using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Application.Modules.SlaAndEscalation.Abstractions;

/// <summary>One inbox row: a Pending request with the ticket context a Department Head needs to decide it.</summary>
public sealed record PendingDowngradeRow(
    PriorityDowngradeRequest Request, string TicketNumber, int TicketDepartmentId);

/// <summary>MVP-ERD.md section 2.27 persistence. Rows are never deleted.</summary>
public interface IPriorityDowngradeRequestRepository
{
    Task<PriorityDowngradeRequest?> GetByIdAsync(long requestId, CancellationToken cancellationToken = default);

    /// <summary>The ticket's one Pending request, if any (a filtered unique index backs the one-pending rule).</summary>
    Task<PriorityDowngradeRequest?> GetPendingForTicketAsync(long ticketId, CancellationToken cancellationToken = default);

    /// <summary>Every request for the ticket, newest first.</summary>
    Task<IReadOnlyList<PriorityDowngradeRequest>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pending requests whose ticket sits in one of <paramref name="departmentIds"/>
    /// (null = every department), oldest first, excluding those past expiry at <paramref name="nowUtc"/>.
    /// </summary>
    Task<(IReadOnlyList<PendingDowngradeRow> Items, int TotalCount)> ListPendingAsync(
        IReadOnlyCollection<int>? departmentIds, DateTime nowUtc, int skip, int take, CancellationToken cancellationToken = default);

    Task AddAsync(PriorityDowngradeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Makes the supplied client RowVersion the entity's original value, so a stale decision fails optimistic concurrency.</summary>
    void SetRowVersion(PriorityDowngradeRequest request, byte[] rowVersion);
}

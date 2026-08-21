using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public enum TicketSortBy
{
    CreatedAtUtc,
    Priority
}

/// <summary>
/// Server-computed query for the ticket queue (MVP-API-Contracts.md §3.2).
/// <see cref="VisibleDepartmentIds"/> is never client-supplied — the
/// application service resolves it from the caller's role/department
/// membership before this query is built, so department visibility is
/// enforced at the query itself, not filtered out after the fact.
/// </summary>
public sealed record TicketQuery(
    IReadOnlyCollection<int>? VisibleDepartmentIds,
    int? DepartmentId,
    int? CategoryId,
    byte? PriorityId,
    TicketStatus? TicketStatus,
    CrmVerificationStatus? VerificationStatus,
    Guid? OwnerEmployeeId,
    string? Search,
    TicketSortBy SortBy,
    bool SortDescending,
    int Page,
    int PageSize);

public sealed record TicketQueryResult(IReadOnlyList<Ticket> Items, int TotalCount);

public interface ITicketRepository
{
    Task<Ticket?> GetByIdAsync(long ticketId, CancellationToken cancellationToken = default);

    Task AddAsync(Ticket ticket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Count of existing tickets whose TicketNumber already starts with
    /// <paramref name="ticketNumberPrefix"/> (e.g. "TG-CS-20260820-") — used
    /// to compute the next per-department-per-day sequence segment
    /// (FR-TKT-01). The unique index on TicketNumber remains the actual
    /// correctness backstop under concurrency (TicketingUnitOfWork retries
    /// on a collision); this count only picks a good first guess.
    /// </summary>
    Task<int> CountByTicketNumberPrefixAsync(string ticketNumberPrefix, CancellationToken cancellationToken = default);

    /// <summary>The ticket queue (MVP-API-Contracts.md §3.2) — paginated, sorted, department-visibility-scoped.</summary>
    Task<TicketQueryResult> SearchAsync(TicketQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Primes EF Core's concurrency check: the client-supplied `If-Match`
    /// value becomes the tracked entity's "original" RowVersion, so
    /// SaveChanges' generated UPDATE includes `WHERE RowVersion = @clientValue`
    /// and fails with <c>DbUpdateConcurrencyException</c>
    /// if another request already changed the row. Must be called before any
    /// mutation on a ticket fetched for an assignment/transfer/status/
    /// resolve/close/reconciliation write.
    /// </summary>
    void SetRowVersion(Ticket ticket, byte[] rowVersion);
}

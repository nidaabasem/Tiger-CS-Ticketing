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

/// <summary>
/// Customer History (Customer → Previous Ticket History, this increment).
/// Exactly one of <see cref="CrmBuyerCustomerId"/>/<see cref="TicketIds"/> is
/// ever meaningful — the CRM-verified identity (<c>CrmBuyerCustomerId</c>)
/// and the phone-snapshot fallback identity are never combined into one
/// query (see <c>CustomerHistoryAppService</c>'s remarks for why: a phone
/// number is not a trusted unique customer identity, so it must never widen
/// a verified customer's own history). <see cref="TicketIds"/> is the
/// fallback path's pre-resolved set — the ticket ids linked from an
/// IntakeRecord recorded against the same phone number
/// (<c>IIntakeRecordRepository.ListLinkedTicketIdsByPhoneNumberAsync</c>) —
/// kept out of this repository so it never needs to join across the
/// IntakeRecord aggregate itself.
/// </summary>
public sealed record CustomerHistoryQuery(
    IReadOnlyCollection<int>? VisibleDepartmentIds,
    int? CrmBuyerCustomerId,
    IReadOnlyCollection<long>? TicketIds,
    long? ExcludeTicketId,
    int Limit);

/// <summary>
/// <see cref="Tickets"/> is the newest-first page (bounded by
/// <see cref="CustomerHistoryQuery.Limit"/>); <see cref="TotalCount"/>/
/// <see cref="OpenCount"/>/<see cref="ClosedCount"/> are computed over every
/// matching row, not just the returned page — "Total Tickets: 6, Open: 2,
/// Closed: 4" must count the customer's whole history even when only the 5
/// most recent rows are shown.
/// </summary>
public sealed record CustomerHistoryQueryResult(
    IReadOnlyList<Ticket> Tickets, int TotalCount, int OpenCount, int ClosedCount);

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
    /// Customer History — every ticket for one customer identity (Section
    /// "Customer → Previous Ticket History", this increment), newest first,
    /// filtered and limited entirely in the query (never loaded in full and
    /// filtered in memory). <see cref="CustomerHistoryQuery.VisibleDepartmentIds"/>
    /// applies the exact same department-visibility scoping as
    /// <see cref="SearchAsync"/> — never client-supplied.
    /// </summary>
    Task<CustomerHistoryQueryResult> SearchCustomerHistoryAsync(CustomerHistoryQuery query, CancellationToken cancellationToken = default);

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

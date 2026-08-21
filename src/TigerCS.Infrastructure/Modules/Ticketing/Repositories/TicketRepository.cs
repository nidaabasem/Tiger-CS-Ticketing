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

    public async Task<TicketQueryResult> SearchAsync(TicketQuery query, CancellationToken cancellationToken = default)
    {
        var filtered = dbContext.Tickets.AsQueryable();

        if (query.VisibleDepartmentIds is not null)
        {
            filtered = filtered.Where(t => query.VisibleDepartmentIds.Contains(t.CurrentDepartmentId));
        }

        if (query.DepartmentId is { } departmentId)
        {
            filtered = filtered.Where(t => t.CurrentDepartmentId == departmentId);
        }

        if (query.CategoryId is { } categoryId)
        {
            filtered = filtered.Where(t => t.CategoryId == categoryId);
        }

        if (query.PriorityId is { } priorityId)
        {
            filtered = filtered.Where(t => t.PriorityId == priorityId);
        }

        if (query.TicketStatus is { } ticketStatus)
        {
            filtered = filtered.Where(t => t.TicketStatus == ticketStatus);
        }

        if (query.VerificationStatus is { } verificationStatus)
        {
            filtered = filtered.Where(t => t.VerificationStatus == verificationStatus);
        }

        if (query.OwnerEmployeeId is { } ownerEmployeeId)
        {
            filtered = filtered.Where(t => t.CurrentOwnerEmployeeId == ownerEmployeeId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            filtered = filtered.Where(t =>
                t.TicketNumber.Contains(query.Search) || t.RequestSummary.Contains(query.Search));
        }

        var totalCount = await filtered.CountAsync(cancellationToken);

        filtered = query.SortBy switch
        {
            TicketSortBy.Priority => query.SortDescending
                ? filtered.OrderByDescending(t => t.PriorityId).ThenByDescending(t => t.CreatedAtUtc)
                : filtered.OrderBy(t => t.PriorityId).ThenByDescending(t => t.CreatedAtUtc),
            _ => query.SortDescending
                ? filtered.OrderByDescending(t => t.CreatedAtUtc)
                : filtered.OrderBy(t => t.CreatedAtUtc)
        };

        var items = await filtered
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new TicketQueryResult(items, totalCount);
    }

    public void SetRowVersion(Ticket ticket, byte[] rowVersion) =>
        dbContext.Entry(ticket).Property(t => t.RowVersion).OriginalValue = rowVersion;
}

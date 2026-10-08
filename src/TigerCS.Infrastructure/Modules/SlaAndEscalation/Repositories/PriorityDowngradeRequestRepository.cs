using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.SlaAndEscalation.Repositories;

public sealed class PriorityDowngradeRequestRepository(TigerCsDbContext dbContext) : IPriorityDowngradeRequestRepository
{
    public Task<PriorityDowngradeRequest?> GetByIdAsync(long requestId, CancellationToken cancellationToken = default) =>
        dbContext.PriorityDowngradeRequests.FirstOrDefaultAsync(r => r.PriorityDowngradeRequestId == requestId, cancellationToken);

    public Task<PriorityDowngradeRequest?> GetPendingForTicketAsync(long ticketId, CancellationToken cancellationToken = default) =>
        dbContext.PriorityDowngradeRequests.FirstOrDefaultAsync(
            r => r.TicketId == ticketId && r.Status == PriorityDowngradeRequestStatus.Pending, cancellationToken);

    public async Task<IReadOnlyList<PriorityDowngradeRequest>> ListByTicketIdAsync(
        long ticketId, CancellationToken cancellationToken = default) =>
        await dbContext.PriorityDowngradeRequests
            .AsNoTracking()
            .Where(r => r.TicketId == ticketId)
            .OrderByDescending(r => r.RequestedAtUtc)
            .ThenByDescending(r => r.PriorityDowngradeRequestId)
            .ToListAsync(cancellationToken);

    public async Task<(IReadOnlyList<PendingDowngradeRow> Items, int TotalCount)> ListPendingAsync(
        IReadOnlyCollection<int>? departmentIds, DateTime nowUtc, int skip, int take, CancellationToken cancellationToken = default)
    {
        var query =
            from r in dbContext.PriorityDowngradeRequests.AsNoTracking()
            join t in dbContext.Tickets.AsNoTracking() on r.TicketId equals t.TicketId
            where r.Status == PriorityDowngradeRequestStatus.Pending && r.ExpiresAtUtc > nowUtc
            select new { Request = r, t.TicketNumber, t.CurrentDepartmentId };

        if (departmentIds is not null)
        {
            var ids = departmentIds.ToArray();
            query = query.Where(x => ids.Contains(x.CurrentDepartmentId));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(x => x.Request.RequestedAtUtc)
            .ThenBy(x => x.Request.PriorityDowngradeRequestId)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return ([.. rows.Select(x => new PendingDowngradeRow(x.Request, x.TicketNumber, x.CurrentDepartmentId))], total);
    }

    public async Task AddAsync(PriorityDowngradeRequest request, CancellationToken cancellationToken = default) =>
        await dbContext.PriorityDowngradeRequests.AddAsync(request, cancellationToken);

    public void SetRowVersion(PriorityDowngradeRequest request, byte[] rowVersion) =>
        dbContext.Entry(request).Property(r => r.RowVersion).OriginalValue = rowVersion;
}

using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Abstractions;
using TigerCS.Domain.Audit;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Audit;

public sealed class AuditEntryReader(TigerCsDbContext dbContext) : IAuditEntryReader
{
    public async Task<(IReadOnlyList<AuditEntry> Items, int TotalCount)> ListForEntityAsync(
        string entityId, IReadOnlyCollection<string> entityTypes, IReadOnlyCollection<string> actions,
        int skip, int take, CancellationToken cancellationToken = default)
    {
        var query = Filter(entityId, entityTypes, actions);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.AuditEntryId)
            .Skip(skip).Take(take)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<AuditEntry?> GetLatestAsync(
        string entityId, IReadOnlyCollection<string> entityTypes, IReadOnlyCollection<string> actions,
        CancellationToken cancellationToken = default) =>
        Filter(entityId, entityTypes, actions)
            .OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.AuditEntryId)
            .FirstOrDefaultAsync(cancellationToken);

    private IQueryable<AuditEntry> Filter(string entityId, IReadOnlyCollection<string> entityTypes, IReadOnlyCollection<string> actions) =>
        dbContext.AuditEntries.AsNoTracking()
            .Where(a => a.EntityId == entityId && entityTypes.Contains(a.EntityType) && actions.Contains(a.Action));
}

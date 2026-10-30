using TigerCS.Application.Abstractions;
using TigerCS.Domain.Audit;

namespace TigerCS.Tests.Administration.Services;

/// <summary>An in-memory audit trail: write through <see cref="Add"/>, read through the same port the Activity History uses.</summary>
public sealed class FakeAuditEntryReader : IAuditEntryReader
{
    private readonly List<AuditEntry> _entries = [];

    public FakeAuditEntryReader Add(Guid? actor, string action, string entityType, Guid entityId, string? before, string? after, DateTime occurredAtUtc)
    {
        _entries.Add(new AuditEntry(actor, action, entityType, entityId.ToString(), before, after, Guid.NewGuid(), occurredAtUtc));
        return this;
    }

    private IEnumerable<AuditEntry> Filter(string entityId, IReadOnlyCollection<string> entityTypes, IReadOnlyCollection<string> actions) =>
        _entries.Where(e => e.EntityId == entityId && entityTypes.Contains(e.EntityType) && actions.Contains(e.Action))
            .OrderByDescending(e => e.OccurredAtUtc);

    public Task<(IReadOnlyList<AuditEntry> Items, int TotalCount)> ListForEntityAsync(
        string entityId, IReadOnlyCollection<string> entityTypes, IReadOnlyCollection<string> actions, int skip, int take, CancellationToken cancellationToken = default)
    {
        var all = Filter(entityId, entityTypes, actions).ToList();
        return Task.FromResult<(IReadOnlyList<AuditEntry>, int)>((all.Skip(skip).Take(take).ToList(), all.Count));
    }

    public Task<AuditEntry?> GetLatestAsync(
        string entityId, IReadOnlyCollection<string> entityTypes, IReadOnlyCollection<string> actions, CancellationToken cancellationToken = default) =>
        Task.FromResult(Filter(entityId, entityTypes, actions).FirstOrDefault());
}

using TigerCS.Domain.Audit;

namespace TigerCS.Application.Abstractions;

/// <summary>
/// Read side of the audit trail (<see cref="IAuditEntryWriter"/> is the write
/// side). Narrow on purpose: a caller names the entity and the exact actions
/// it may see, so no screen can browse the whole trail.
/// </summary>
public interface IAuditEntryReader
{
    /// <summary>Newest first. <paramref name="entityTypes"/> and <paramref name="actions"/> both restrict the result.</summary>
    Task<(IReadOnlyList<AuditEntry> Items, int TotalCount)> ListForEntityAsync(
        string entityId, IReadOnlyCollection<string> entityTypes, IReadOnlyCollection<string> actions,
        int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>The most recent entry for the entity among the given actions, or null when none was ever recorded.</summary>
    Task<AuditEntry?> GetLatestAsync(
        string entityId, IReadOnlyCollection<string> entityTypes, IReadOnlyCollection<string> actions,
        CancellationToken cancellationToken = default);
}

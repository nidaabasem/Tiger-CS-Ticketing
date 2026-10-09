using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface IPriorityRepository
{
    Task<Priority?> GetByIdAsync(byte priorityId, CancellationToken cancellationToken = default);

    /// <summary>The priority whose configured name matches (case-insensitive), or null.</summary>
    Task<Priority?> GetByNameAsync(string name, CancellationToken cancellationToken = default);
}

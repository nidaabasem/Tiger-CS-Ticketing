using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface IPriorityRepository
{
    Task<Priority?> GetByIdAsync(byte priorityId, CancellationToken cancellationToken = default);
}

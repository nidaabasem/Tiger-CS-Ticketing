using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class PriorityRepository(TigerCsDbContext dbContext) : IPriorityRepository
{
    public Task<Priority?> GetByIdAsync(byte priorityId, CancellationToken cancellationToken = default) =>
        dbContext.Priorities.FirstOrDefaultAsync(p => p.PriorityId == priorityId, cancellationToken);
}

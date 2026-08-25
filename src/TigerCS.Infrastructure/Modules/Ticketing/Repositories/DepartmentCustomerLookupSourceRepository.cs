using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class DepartmentCustomerLookupSourceRepository(TigerCsDbContext dbContext) : IDepartmentCustomerLookupSourceRepository
{
    public async Task<IReadOnlyCollection<CustomerLookupSource>> GetSourcesForDepartmentAsync(
        int departmentId, CancellationToken cancellationToken = default) =>
        await dbContext.DepartmentCustomerLookupSources
            .Where(d => d.DepartmentId == departmentId)
            .Select(d => d.Source)
            .ToListAsync(cancellationToken);
}

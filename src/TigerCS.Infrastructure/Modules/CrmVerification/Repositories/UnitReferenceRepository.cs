using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.CrmVerification.Abstractions;
using TigerCS.Domain.Modules.CrmVerification;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.CrmVerification.Repositories;

public sealed class UnitReferenceRepository(TigerCsDbContext dbContext) : IUnitReferenceRepository
{
    public Task<UnitReference?> GetByCrmUnitIdAsync(string crmUnitId, CancellationToken cancellationToken = default) =>
        dbContext.UnitReferences.FirstOrDefaultAsync(u => u.CrmUnitId == crmUnitId, cancellationToken);

    public Task<UnitReference?> GetByIdAsync(int unitReferenceId, CancellationToken cancellationToken = default) =>
        dbContext.UnitReferences.FirstOrDefaultAsync(u => u.UnitReferenceId == unitReferenceId, cancellationToken);

    public async Task AddAsync(UnitReference unitReference, CancellationToken cancellationToken = default) =>
        await dbContext.UnitReferences.AddAsync(unitReference, cancellationToken);
}

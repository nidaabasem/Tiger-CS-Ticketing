using TigerCS.Application.Modules.CrmVerification.Abstractions;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.CrmVerification.Repositories;

public sealed class CrmVerificationUnitOfWork(TigerCsDbContext dbContext) : ICrmVerificationUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => dbContext.SaveChangesAsync(cancellationToken);
}

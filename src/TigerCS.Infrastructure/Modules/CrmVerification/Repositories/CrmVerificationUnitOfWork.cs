using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.CrmVerification.Abstractions;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.CrmVerification.Repositories;

public sealed class CrmVerificationUnitOfWork(TigerCsDbContext dbContext) : ICrmVerificationUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Translated so the Application layer (VerificationSessionAppService's
            // concurrent-duplicate-request handling) never depends on EF Core
            // types directly — matches how a real uniqueness-constraint race
            // (e.g. two concurrent requests with the same Idempotency-Key)
            // surfaces on both the InMemory provider (tests) and SQL Server.
            throw new DuplicateWriteException(ex);
        }
    }
}

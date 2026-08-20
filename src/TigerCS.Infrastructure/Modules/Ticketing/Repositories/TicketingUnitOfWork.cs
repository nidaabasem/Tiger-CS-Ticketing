using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

/// <summary>Mirrors CustomerVerificationUnitOfWork's translation exactly — see its remarks for the SQL-Server-only detection rationale and the InMemory-provider caveat. Additionally translates a lost optimistic-concurrency race on VerificationSession consumption (see ITicketingUnitOfWork's remarks and TigerCsDbContext's supplemental concurrency-token configuration).</summary>
public sealed class TicketingUnitOfWork(TigerCsDbContext dbContext) : ITicketingUnitOfWork
{
    public async Task<ITicketingTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        new EfTicketingTransaction(await dbContext.Database.BeginTransactionAsync(cancellationToken));

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Not a unique-constraint violation (that case is handled below)
            // — this is EF Core's own client-side detection that an UPDATE
            // affected zero rows because the concurrency-token value
            // (VerificationSessions.Status) had already changed. The only
            // concurrency token configured anywhere in this module's model
            // is that one, so in practice this always means: another
            // request already consumed the same session first.
            throw new VerificationSessionConcurrentlyConsumedException(ex);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            throw new DuplicateWriteException(ex);
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SqlException { Number: 2601 or 2627 })
            {
                return true;
            }
        }

        return false;
    }

    private sealed class EfTicketingTransaction(IDbContextTransaction inner) : ITicketingTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) => inner.CommitAsync(cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

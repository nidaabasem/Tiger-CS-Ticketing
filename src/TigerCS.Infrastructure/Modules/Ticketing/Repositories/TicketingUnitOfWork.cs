using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

/// <summary>Mirrors CustomerVerificationUnitOfWork's translation exactly — see its remarks for the SQL-Server-only detection rationale and the InMemory-provider caveat. Additionally translates a lost optimistic-concurrency race on VerificationSession consumption or on a Ticket's own RowVersion (see ITicketingUnitOfWork's remarks and TigerCsDbContext's supplemental concurrency-token configuration).</summary>
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
            // EF Core's own client-side detection that an UPDATE affected
            // zero rows because a concurrency-token value had already
            // changed. Two independent tokens exist in this module's model:
            // VerificationSessions.Status (single-use session consumption)
            // and Tickets.RowVersion (assignment/transfer/status-change/
            // resolve/close/reconciliation) — inspect which entity type
            // actually lost the race so the caller gets the right, specific
            // exception rather than a misleading one.
            if (ex.Entries.Any(e => e.Entity is VerificationSession))
            {
                throw new VerificationSessionConcurrentlyConsumedException(ex);
            }

            throw new TicketConcurrentlyModifiedException(ex);
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

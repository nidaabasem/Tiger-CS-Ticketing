using Microsoft.Data.SqlClient;
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
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Translated so the Application layer (VerificationSessionAppService's
            // and CrmVerificationAppService's concurrent-write recovery)
            // never depends on EF Core/SqlClient types directly. Only a
            // genuine unique-constraint/duplicate-key violation is
            // translated — any other DbUpdateException (FK violation,
            // required-field violation, a real connectivity failure, etc.)
            // propagates unchanged, since none of those are safe to recover
            // from by re-querying and returning "the existing row."
            throw new DuplicateWriteException(ex);
        }
    }

    /// <summary>
    /// SQL Server only — what Program.cs actually configures in every real
    /// deployment: 2601 (Cannot insert duplicate key row... unique index)
    /// and 2627 (Violation of ... constraint... Cannot insert duplicate
    /// key) are the two error numbers a duplicate-key write can raise,
    /// regardless of how deep EF Core's own wrapping nests the SqlException.
    ///
    /// <para>
    /// There is deliberately no EF Core InMemory-specific detection branch
    /// here (an earlier version of this method guessed at one). Empirically
    /// verified — not assumed — that the InMemory provider used by this
    /// project's test suite does not enforce a unique index at all under
    /// genuine concurrent writes from separate DbContext instances, neither
    /// the filtered VerificationSessions index nor a plain one like
    /// UnitReferences.CrmUnitId/ContactReferences.CrmContactId: two
    /// concurrent inserts of the same key both silently succeed, producing
    /// two rows, rather than the second raising any exception to classify.
    /// See CrmVerificationUpsertConcurrencyTests' and
    /// VerificationSessionConfiguration's remarks for the full
    /// consequence — recovery here is proven only where SQL Server's real
    /// constraint can actually fire: unit tests that simulate
    /// DuplicateWriteException directly (bypassing this method), and the
    /// real-SQL-Server db-migration-validation.yml CI job for the indexes'
    /// existence/shape.
    /// </para>
    /// </summary>
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
}

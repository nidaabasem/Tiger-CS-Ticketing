namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>Commits everything added/changed through this module's repositories in one transaction. Mirrors ICustomerVerificationUnitOfWork's shape.</summary>
public interface ITicketingUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Wraps every <see cref="SaveChangesAsync"/> call made against the
    /// returned <see cref="ITicketingTransaction"/>'s scope in one real
    /// database transaction — needed because ticket creation genuinely
    /// requires two <see cref="SaveChangesAsync"/> calls (the <c>Ticket</c>
    /// row must be inserted, and its database-generated <c>TicketId</c>
    /// known, before the requester snapshot/status-history/audit rows and
    /// the <c>VerificationSession</c> consumption can be written), and
    /// without an explicit transaction a failure in the second call (e.g.
    /// <see cref="VerificationSessionConcurrentlyConsumedException"/>)
    /// would otherwise leave the first call's <c>Ticket</c> insert
    /// permanently committed and orphaned.
    /// </summary>
    Task<ITicketingTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
}

/// <summary>A single unit of atomicity spanning multiple <see cref="ITicketingUnitOfWork.SaveChangesAsync"/> calls. Disposing without committing rolls back (standard ADO.NET/EF Core transaction semantics) — callers that return early on a handled failure do not need to call <see cref="RollbackAsync"/> explicitly, though doing so is harmless and clearer at the call site.</summary>
public interface ITicketingTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}

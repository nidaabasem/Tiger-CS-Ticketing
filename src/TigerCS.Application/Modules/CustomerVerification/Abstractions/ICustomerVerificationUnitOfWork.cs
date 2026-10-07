namespace TigerCS.Application.Modules.CustomerVerification.Abstractions;

public interface ICustomerVerificationUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets every staged, unsaved change (and any entity read through this
    /// unit of work) after a <see cref="ConcurrentWriteException"/>, so the
    /// caller can re-read the row as it is really stored instead of
    /// re-evaluating its own half-applied copy.
    /// </summary>
    void DiscardPendingChanges();
}

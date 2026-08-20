namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>Commits everything added/changed through this module's repositories in one transaction. Mirrors ICustomerVerificationUnitOfWork's shape.</summary>
public interface ITicketingUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

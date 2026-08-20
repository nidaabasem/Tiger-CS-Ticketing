namespace TigerCS.Application.Modules.CustomerVerification.Abstractions;

public interface ICustomerVerificationUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

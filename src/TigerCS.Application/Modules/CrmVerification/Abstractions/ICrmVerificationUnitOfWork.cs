namespace TigerCS.Application.Modules.CrmVerification.Abstractions;

public interface ICrmVerificationUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

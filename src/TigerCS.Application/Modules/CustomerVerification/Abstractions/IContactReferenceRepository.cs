using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Application.Modules.CustomerVerification.Abstractions;

/// <summary>Application-layer port over ContactReference persistence; implemented in Infrastructure with EF Core.</summary>
public interface IContactReferenceRepository
{
    Task<IReadOnlyList<ContactReference>> GetByUnitReferenceIdAsync(
        int unitReferenceId, CancellationToken cancellationToken = default);

    Task<ContactReference?> GetByCrmContactIdAsync(string crmContactId, CancellationToken cancellationToken = default);

    Task<ContactReference?> GetByIdAsync(int contactReferenceId, CancellationToken cancellationToken = default);

    Task AddAsync(ContactReference contactReference, CancellationToken cancellationToken = default);
}

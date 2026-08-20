using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Application.Modules.CustomerVerification.Abstractions;

/// <summary>Application-layer port over UnitReference persistence; implemented in Infrastructure with EF Core.</summary>
public interface IUnitReferenceRepository
{
    Task<UnitReference?> GetByCrmUnitIdAsync(string crmUnitId, CancellationToken cancellationToken = default);

    Task<UnitReference?> GetByIdAsync(int unitReferenceId, CancellationToken cancellationToken = default);

    Task AddAsync(UnitReference unitReference, CancellationToken cancellationToken = default);
}

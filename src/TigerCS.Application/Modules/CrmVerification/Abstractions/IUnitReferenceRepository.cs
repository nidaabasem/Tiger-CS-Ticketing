using TigerCS.Domain.Modules.CrmVerification;

namespace TigerCS.Application.Modules.CrmVerification.Abstractions;

/// <summary>Application-layer port over UnitReference persistence; implemented in Infrastructure with EF Core.</summary>
public interface IUnitReferenceRepository
{
    Task<UnitReference?> GetByCrmUnitIdAsync(string crmUnitId, CancellationToken cancellationToken = default);

    Task<UnitReference?> GetByIdAsync(int unitReferenceId, CancellationToken cancellationToken = default);

    Task AddAsync(UnitReference unitReference, CancellationToken cancellationToken = default);
}

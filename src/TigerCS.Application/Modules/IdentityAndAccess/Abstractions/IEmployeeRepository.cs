using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.IdentityAndAccess.Abstractions;

/// <summary>Application-layer port over Employee persistence; implemented in Infrastructure with EF Core.</summary>
public interface IEmployeeRepository
{
    Task<Employee?> GetByIdAsync(Guid employeeId, CancellationToken cancellationToken = default);

    Task AddAsync(Employee employee, CancellationToken cancellationToken = default);

    /// <summary>Counts active employees currently holding the given role (used by the last-admin guard).</summary>
    Task<int> CountActiveInRoleAsync(string roleName, CancellationToken cancellationToken = default);
}

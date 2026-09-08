using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.IdentityAndAccess.Abstractions;

/// <summary>Application-layer port over Employee persistence; implemented in Infrastructure with EF Core.</summary>
public interface IEmployeeRepository
{
    Task<Employee?> GetByIdAsync(Guid employeeId, CancellationToken cancellationToken = default);

    Task AddAsync(Employee employee, CancellationToken cancellationToken = default);

    /// <summary>Counts active employees currently holding the given role (used by the last-admin guard).</summary>
    Task<int> CountActiveInRoleAsync(string roleName, CancellationToken cancellationToken = default);

    /// <summary>Administration listing, ordered by display name. <paramref name="employeeIds"/> narrows to a set (an account search); null means every employee.</summary>
    Task<IReadOnlyList<Employee>> ListAsync(
        bool includeInactive, IReadOnlyCollection<Guid>? employeeIds, CancellationToken cancellationToken = default);

    /// <summary>Whether any ticket, assignment, approval or audit row references this employee — the reason deactivation, never deletion, is the retirement path.</summary>
    Task<bool> IsReferencedByHistoryAsync(Guid employeeId, CancellationToken cancellationToken = default);
}

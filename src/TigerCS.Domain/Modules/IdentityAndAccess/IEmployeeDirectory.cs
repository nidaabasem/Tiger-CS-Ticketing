namespace TigerCS.Domain.Modules.IdentityAndAccess;

/// <summary>
/// Read access to Employee/Department data (Module-Design.md's "Identity and
/// Access" public interface) — the seam other modules use instead of
/// depending on Infrastructure/EF Core directly.
/// </summary>
public interface IEmployeeDirectory
{
    Task<Employee?> FindByIdAsync(Guid employeeId, CancellationToken cancellationToken = default);

    Task<bool> IsActiveAsync(Guid employeeId, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<UserDepartmentAssignment>> GetDepartmentAssignmentsAsync(
        Guid employeeId, CancellationToken cancellationToken = default);
}

using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.IdentityAndAccess.Abstractions;

public interface IUserDepartmentAssignmentRepository
{
    Task<IReadOnlyCollection<UserDepartmentAssignment>> GetByEmployeeIdAsync(
        Guid employeeId, CancellationToken cancellationToken = default);

    Task<UserDepartmentAssignment?> GetPrimaryAsync(Guid employeeId, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(Guid employeeId, int departmentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<UserDepartmentAssignment>> GetByDepartmentIdAsync(
        int departmentId, bool activeEmployeesOnly, CancellationToken cancellationToken = default);

    Task AddAsync(UserDepartmentAssignment assignment, CancellationToken cancellationToken = default);
}

/// <summary>Commits changes made through the Identity and Access repositories in this request.</summary>
public interface IIdentityUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

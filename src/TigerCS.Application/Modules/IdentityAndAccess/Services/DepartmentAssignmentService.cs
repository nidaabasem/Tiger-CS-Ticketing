using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.IdentityAndAccess.Services;

/// <summary>
/// Enforces the UserDepartmentAssignments invariants (MVP-ERD.md §0.1,
/// MVP-Data-Dictionary.md §2.4): an employee may belong to multiple
/// departments, at most one of which is primary; only active departments
/// and active employees may receive new assignments.
/// </summary>
public sealed class DepartmentAssignmentService(
    IEmployeeRepository employeeRepository,
    IDepartmentRepository departmentRepository,
    IUserDepartmentAssignmentRepository assignmentRepository,
    IIdentityUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Creates a new assignment. If <paramref name="isPrimary"/> is true and the
    /// employee already has a different primary department, that prior
    /// assignment is demoted first so the "at most one primary" invariant
    /// always holds after this call returns.
    /// </summary>
    public async Task<UserDepartmentAssignment> AssignAsync(
        Guid employeeId,
        int departmentId,
        bool isPrimary,
        Guid? assignedByEmployeeId,
        CancellationToken cancellationToken = default)
    {
        var employee = await employeeRepository.GetByIdAsync(employeeId, cancellationToken)
            ?? throw new KeyNotFoundException($"Employee {employeeId} was not found.");
        if (!employee.IsActive)
        {
            throw new InactiveEmployeeAssignmentException(employeeId);
        }

        var department = await departmentRepository.GetByIdAsync(departmentId, cancellationToken)
            ?? throw new KeyNotFoundException($"Department {departmentId} was not found.");
        if (!department.IsActive)
        {
            throw new InactiveDepartmentAssignmentException(departmentId);
        }

        if (await assignmentRepository.ExistsAsync(employeeId, departmentId, cancellationToken))
        {
            throw new DuplicateDepartmentAssignmentException(employeeId, departmentId);
        }

        if (isPrimary)
        {
            var existingPrimary = await assignmentRepository.GetPrimaryAsync(employeeId, cancellationToken);
            existingPrimary?.ClearPrimary();
        }

        var assignment = new UserDepartmentAssignment(
            employeeId, departmentId, isPrimary, timeProvider.GetUtcNow().UtcDateTime, assignedByEmployeeId);

        await assignmentRepository.AddAsync(assignment, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return assignment;
    }
}

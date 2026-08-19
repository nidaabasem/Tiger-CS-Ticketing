namespace TigerCS.Domain.Modules.IdentityAndAccess;

/// <summary>Base type for violations of the department-assignment invariants.</summary>
public abstract class DepartmentAssignmentException(string message) : Exception(message);

public sealed class InactiveDepartmentAssignmentException(int departmentId)
    : DepartmentAssignmentException($"Department {departmentId} is not active and cannot receive new assignments.")
{
    public int DepartmentId { get; } = departmentId;
}

public sealed class InactiveEmployeeAssignmentException(Guid employeeId)
    : DepartmentAssignmentException($"Employee {employeeId} is not active and cannot receive new assignments.")
{
    public Guid EmployeeId { get; } = employeeId;
}

public sealed class DuplicateDepartmentAssignmentException(Guid employeeId, int departmentId)
    : DepartmentAssignmentException($"Employee {employeeId} already has an assignment to department {departmentId}.")
{
    public Guid EmployeeId { get; } = employeeId;
    public int DepartmentId { get; } = departmentId;
}

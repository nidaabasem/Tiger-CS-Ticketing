namespace TigerCS.Domain.Modules.IdentityAndAccess;

/// <summary>
/// Many-to-many Employee&#8596;Department membership, with at most one primary
/// department per employee (MVP-ERD.md §0.1, MVP-Data-Dictionary.md §2.4).
/// The "exactly one primary row per EmployeeId" invariant is app-enforced
/// (see DepartmentAssignmentService) and backed by a filtered unique index
/// on (EmployeeId) WHERE IsPrimary = 1.
/// </summary>
public class UserDepartmentAssignment
{
    public int UserDepartmentAssignmentId { get; private set; }
    public Guid EmployeeId { get; private set; }
    public int DepartmentId { get; private set; }
    public bool IsPrimary { get; private set; }
    public DateTime AssignedAtUtc { get; private set; }
    public Guid? AssignedByEmployeeId { get; private set; }

    public Employee Employee { get; private set; } = null!;
    public Department Department { get; private set; } = null!;

    private UserDepartmentAssignment() { }

    public UserDepartmentAssignment(
        Guid employeeId,
        int departmentId,
        bool isPrimary,
        DateTime assignedAtUtc,
        Guid? assignedByEmployeeId)
    {
        EmployeeId = employeeId;
        DepartmentId = departmentId;
        IsPrimary = isPrimary;
        AssignedAtUtc = assignedAtUtc;
        AssignedByEmployeeId = assignedByEmployeeId;
    }

    public void MakePrimary() => IsPrimary = true;

    public void ClearPrimary() => IsPrimary = false;
}

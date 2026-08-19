namespace TigerCS.Domain.Modules.IdentityAndAccess;

/// <summary>
/// Domain extension of AspNetUsers (ADR-0004, MVP-Data-Dictionary.md §2.2).
/// EmployeeId is both the PK here and the FK to AspNetUsers.Id (identifying
/// 1:1 relationship) — never a hard delete; DeactivatedAtUtc is the only
/// removal path (FR-ADM-02).
/// </summary>
public class Employee
{
    public Guid EmployeeId { get; private set; }
    public string DisplayName { get; private set; } = string.Empty;
    public bool IsGeynessStaff { get; private set; }
    public DateTime? DeactivatedAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public bool IsActive => DeactivatedAtUtc is null;

    private readonly List<UserDepartmentAssignment> _departmentAssignments = [];
    public IReadOnlyCollection<UserDepartmentAssignment> DepartmentAssignments => _departmentAssignments;

    private Employee() { }

    public Employee(Guid employeeId, string displayName, bool isGeynessStaff, DateTime createdAtUtc)
    {
        if (employeeId == Guid.Empty)
        {
            throw new ArgumentException("EmployeeId must match an existing AspNetUsers.Id.", nameof(employeeId));
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("DisplayName is required.", nameof(displayName));
        }

        EmployeeId = employeeId;
        DisplayName = displayName;
        IsGeynessStaff = isGeynessStaff;
        CreatedAtUtc = createdAtUtc;
    }

    public void Deactivate(DateTime deactivatedAtUtc)
    {
        DeactivatedAtUtc = deactivatedAtUtc;
    }

    public void Reactivate()
    {
        DeactivatedAtUtc = null;
    }
}

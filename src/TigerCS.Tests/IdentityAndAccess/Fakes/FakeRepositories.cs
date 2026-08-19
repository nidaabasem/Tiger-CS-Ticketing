using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.IdentityAndAccess.Fakes;

public sealed class FakeEmployeeRepository : IEmployeeRepository
{
    private readonly Dictionary<Guid, Employee> _employees = [];
    private readonly Dictionary<Guid, List<string>> _rolesByEmployeeId = [];

    public FakeEmployeeRepository Add(Employee employee, params string[] roles)
    {
        _employees[employee.EmployeeId] = employee;
        _rolesByEmployeeId[employee.EmployeeId] = roles.ToList();
        return this;
    }

    public Task<Employee?> GetByIdAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_employees.GetValueOrDefault(employeeId));

    public Task AddAsync(Employee employee, CancellationToken cancellationToken = default)
    {
        _employees[employee.EmployeeId] = employee;
        return Task.CompletedTask;
    }

    public Task<int> CountActiveInRoleAsync(string roleName, CancellationToken cancellationToken = default)
    {
        var count = _employees.Values.Count(e =>
            e.IsActive && _rolesByEmployeeId.TryGetValue(e.EmployeeId, out var roles) && roles.Contains(roleName));
        return Task.FromResult(count);
    }
}

public sealed class FakeDepartmentRepository : IDepartmentRepository
{
    private readonly Dictionary<int, Department> _departments = [];
    private int _nextId = 1;

    public Department AddDepartment(string name, string code, bool isActive = true)
    {
        var department = new Department(name, code, isActive);
        typeof(Department).GetProperty(nameof(Department.DepartmentId))!.SetValue(department, _nextId++);
        _departments[department.DepartmentId] = department;
        return department;
    }

    public Task<Department?> GetByIdAsync(int departmentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_departments.GetValueOrDefault(departmentId));

    public Task<IReadOnlyCollection<Department>> ListAsync(bool activeOnly, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<Department>>(
            _departments.Values.Where(d => !activeOnly || d.IsActive).ToList());
}

public sealed class FakeUserDepartmentAssignmentRepository : IUserDepartmentAssignmentRepository
{
    public readonly List<UserDepartmentAssignment> Assignments = [];

    public Task<IReadOnlyCollection<UserDepartmentAssignment>> GetByEmployeeIdAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<UserDepartmentAssignment>>(
            Assignments.Where(a => a.EmployeeId == employeeId).ToList());

    public Task<UserDepartmentAssignment?> GetPrimaryAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Assignments.FirstOrDefault(a => a.EmployeeId == employeeId && a.IsPrimary));

    public Task<bool> ExistsAsync(Guid employeeId, int departmentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Assignments.Any(a => a.EmployeeId == employeeId && a.DepartmentId == departmentId));

    public Task<IReadOnlyCollection<UserDepartmentAssignment>> GetByDepartmentIdAsync(
        int departmentId, bool activeEmployeesOnly, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<UserDepartmentAssignment>>(
            Assignments.Where(a => a.DepartmentId == departmentId).ToList());

    public Task AddAsync(UserDepartmentAssignment assignment, CancellationToken cancellationToken = default)
    {
        Assignments.Add(assignment);
        return Task.CompletedTask;
    }
}

public sealed class FakeIdentityUnitOfWork : IIdentityUnitOfWork
{
    public int SaveChangesCallCount { get; private set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveChangesCallCount++;
        return Task.CompletedTask;
    }
}

public sealed class FakeUserRoleReader : IUserRoleReader
{
    private readonly Dictionary<Guid, List<string>> _roles = [];

    public FakeUserRoleReader Add(Guid employeeId, params string[] roles)
    {
        _roles[employeeId] = roles.ToList();
        return this;
    }

    public Task<IReadOnlyCollection<string>> GetRolesAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<string>>(_roles.GetValueOrDefault(employeeId, []));
}

using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Fakes;

namespace TigerCS.Tests.IdentityAndAccess.Services;

public class DepartmentAssignmentServiceTests
{
    private static (DepartmentAssignmentService Service, FakeEmployeeRepository Employees, FakeDepartmentRepository Departments,
        FakeUserDepartmentAssignmentRepository Assignments) CreateSut()
    {
        var employees = new FakeEmployeeRepository();
        var departments = new FakeDepartmentRepository();
        var assignments = new FakeUserDepartmentAssignmentRepository();
        var unitOfWork = new FakeIdentityUnitOfWork();
        var service = new DepartmentAssignmentService(
            employees, departments, assignments, unitOfWork, TimeProvider.System);
        return (service, employees, departments, assignments);
    }

    [Fact]
    public async Task AssignAsync_NewPrimary_DemotesPreviousPrimary()
    {
        var (service, employees, departments, assignments) = CreateSut();
        var employee = new Employee(Guid.NewGuid(), "J. Smith", isGeynessStaff: false, DateTime.UtcNow);
        employees.Add(employee);
        var deptA = departments.AddDepartment("Customer Service", "CS");
        var deptB = departments.AddDepartment("Facilities Management", "FM");

        var first = await service.AssignAsync(employee.EmployeeId, deptA.DepartmentId, isPrimary: true, assignedByEmployeeId: null);
        Assert.True(first.IsPrimary);

        var second = await service.AssignAsync(employee.EmployeeId, deptB.DepartmentId, isPrimary: true, assignedByEmployeeId: null);

        Assert.True(second.IsPrimary);
        Assert.False(first.IsPrimary);
        Assert.Single(assignments.Assignments, a => a.IsPrimary);
    }

    [Fact]
    public async Task AssignAsync_SecondaryDepartment_DoesNotDisturbExistingPrimary()
    {
        var (service, employees, departments, assignments) = CreateSut();
        var employee = new Employee(Guid.NewGuid(), "J. Smith", isGeynessStaff: false, DateTime.UtcNow);
        employees.Add(employee);
        var primary = departments.AddDepartment("Customer Service", "CS");
        var secondary = departments.AddDepartment("Finance", "FIN");

        var primaryAssignment = await service.AssignAsync(employee.EmployeeId, primary.DepartmentId, isPrimary: true, null);
        await service.AssignAsync(employee.EmployeeId, secondary.DepartmentId, isPrimary: false, null);

        Assert.True(primaryAssignment.IsPrimary);
        Assert.Equal(2, assignments.Assignments.Count(a => a.EmployeeId == employee.EmployeeId));
        Assert.Single(assignments.Assignments, a => a.IsPrimary);
    }

    [Fact]
    public async Task AssignAsync_InactiveDepartment_Throws()
    {
        var (service, employees, departments, _) = CreateSut();
        var employee = new Employee(Guid.NewGuid(), "J. Smith", isGeynessStaff: false, DateTime.UtcNow);
        employees.Add(employee);
        var inactive = departments.AddDepartment("Legacy Dept", "LGY", isActive: false);

        await Assert.ThrowsAsync<InactiveDepartmentAssignmentException>(() =>
            service.AssignAsync(employee.EmployeeId, inactive.DepartmentId, isPrimary: true, null));
    }

    [Fact]
    public async Task AssignAsync_InactiveEmployee_Throws()
    {
        var (service, employees, departments, _) = CreateSut();
        var employee = new Employee(Guid.NewGuid(), "J. Smith", isGeynessStaff: false, DateTime.UtcNow);
        employee.Deactivate(DateTime.UtcNow);
        employees.Add(employee);
        var department = departments.AddDepartment("Customer Service", "CS");

        await Assert.ThrowsAsync<InactiveEmployeeAssignmentException>(() =>
            service.AssignAsync(employee.EmployeeId, department.DepartmentId, isPrimary: true, null));
    }

    [Fact]
    public async Task AssignAsync_DuplicateDepartment_Throws()
    {
        var (service, employees, departments, _) = CreateSut();
        var employee = new Employee(Guid.NewGuid(), "J. Smith", isGeynessStaff: false, DateTime.UtcNow);
        employees.Add(employee);
        var department = departments.AddDepartment("Customer Service", "CS");

        await service.AssignAsync(employee.EmployeeId, department.DepartmentId, isPrimary: true, null);

        await Assert.ThrowsAsync<DuplicateDepartmentAssignmentException>(() =>
            service.AssignAsync(employee.EmployeeId, department.DepartmentId, isPrimary: false, null));
    }

    [Fact]
    public async Task AssignAsync_UnknownEmployee_ThrowsKeyNotFound()
    {
        var (service, _, departments, _) = CreateSut();
        var department = departments.AddDepartment("Customer Service", "CS");

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.AssignAsync(Guid.NewGuid(), department.DepartmentId, isPrimary: true, null));
    }
}

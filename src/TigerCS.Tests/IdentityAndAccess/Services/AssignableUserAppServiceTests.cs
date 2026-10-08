using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Fakes;

namespace TigerCS.Tests.IdentityAndAccess.Services;

/// <summary>
/// The cross-department assignee directory a CS Manager reads: CS Agents,
/// Call Center staff (CS Agents in department CC) and other departments' staff
/// are all listed with their roles and memberships; a configured service
/// identity (the Genesys account) is not a person and is never offered.
/// </summary>
public class AssignableUserAppServiceTests
{
    private sealed class Registry(params Guid[] ids) : IServiceIdentityRegistry
    {
        public IReadOnlyCollection<Guid> ServiceIdentityIds { get; } = ids;

        public bool IsServiceIdentity(Guid employeeId) => ServiceIdentityIds.Contains(employeeId);
    }

    private static readonly Guid Amina = Guid.NewGuid();   // CS Agent, Customer Service
    private static readonly Guid Bilal = Guid.NewGuid();   // CS Agent, Call Center
    private static readonly Guid Omar = Guid.NewGuid();    // Department Employee, Operations
    private static readonly Guid Genesys = Guid.NewGuid(); // service account, CS Agent in Call Center

    private static (FakeUserDepartmentAssignmentRepository Assignments, FakeUserRoleReader Roles) Seed()
    {
        var assignments = new FakeUserDepartmentAssignmentRepository();
        var roles = new FakeUserRoleReader();

        void Add(Guid id, string name, int departmentId, string departmentName, string role)
        {
            var assignment = new UserDepartmentAssignment(id, departmentId, true, DateTime.UtcNow, null);
            typeof(UserDepartmentAssignment).GetProperty(nameof(UserDepartmentAssignment.Employee))!
                .SetValue(assignment, new Employee(id, name, false, DateTime.UtcNow));
            typeof(UserDepartmentAssignment).GetProperty(nameof(UserDepartmentAssignment.Department))!
                .SetValue(assignment, new Department(departmentName, departmentName[..2].ToUpperInvariant()));
            assignments.Assignments.Add(assignment);
            roles.Add(id, role);
        }

        Add(Amina, "Amina Agent", 1, "Customer Service", Domain.Modules.IdentityAndAccess.Roles.CsAgent);
        Add(Bilal, "Bilal Caller", 2, "Call Center", Domain.Modules.IdentityAndAccess.Roles.CsAgent);
        Add(Omar, "Omar Ops", 3, "Operations", Domain.Modules.IdentityAndAccess.Roles.DepartmentEmployee);
        Add(Genesys, "Genesys Integration", 2, "Call Center", Domain.Modules.IdentityAndAccess.Roles.CsAgent);
        return (assignments, roles);
    }

    [Fact]
    public async Task ListsCsAgents_CallCenterStaff_AndOtherDepartments_WithRolesAndDepartments()
    {
        var (assignments, roles) = Seed();

        var result = await new AssignableUserAppService(assignments, roles).ListAsync();

        Assert.Contains(result, u => u.EmployeeId == Amina && u.Roles.Contains(Domain.Modules.IdentityAndAccess.Roles.CsAgent));
        var callCenter = Assert.Single(result, u => u.EmployeeId == Bilal);
        Assert.Contains(callCenter.Departments, d => d.Name == "Call Center");
        Assert.Contains(result, u => u.EmployeeId == Omar);
    }

    [Fact]
    public async Task ExcludesTheConfiguredServiceIdentity()
    {
        var (assignments, roles) = Seed();

        var result = await new AssignableUserAppService(assignments, roles, new Registry(Genesys)).ListAsync();

        Assert.DoesNotContain(result, u => u.EmployeeId == Genesys);
        Assert.Equal(3, result.Count);
    }
}

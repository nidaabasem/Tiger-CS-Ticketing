using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.Administration.Services;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;

namespace TigerCS.Tests.Administration.Services;

public class AdminUserAppServiceTests
{
    private static readonly Guid Admin = Guid.NewGuid();

    private sealed record Fixture(
        AdminUserAppService Service,
        FakeEmployeeRepository Employees,
        FakeUserAccountManager Accounts,
        FakeDepartmentRepository Departments,
        FakeUserDepartmentAssignmentRepository Assignments,
        FakeAuditEntryWriter Audit);

    private static Fixture Create()
    {
        var employees = new FakeEmployeeRepository();
        var accounts = new FakeUserAccountManager();
        var departments = new FakeDepartmentRepository();
        var assignments = new FakeUserDepartmentAssignmentRepository();
        var unitOfWork = new FakeIdentityUnitOfWork();
        var audit = new FakeAuditEntryWriter();

        var assignmentService = new DepartmentAssignmentService(employees, departments, assignments, unitOfWork, TimeProvider.System);
        var activation = new UserActivationAppService(employees, accounts, unitOfWork, TimeProvider.System);
        var service = new AdminUserAppService(
            employees, accounts, accounts, assignments, departments, assignmentService, activation, unitOfWork, audit, TimeProvider.System);
        return new Fixture(service, employees, accounts, departments, assignments, audit);
    }

    private static Guid SeedUser(Fixture f, string name, params string[] roles)
    {
        var id = Guid.NewGuid();
        f.Employees.Add(new Employee(id, name, false, DateTime.UtcNow), roles);
        f.Accounts.Add(id, name.Replace(' ', '.').ToLowerInvariant(), roles);
        return id;
    }

    [Fact]
    public async Task Create_uses_identity_for_the_account_and_records_roles_and_primary_department()
    {
        var f = Create();
        var collections = f.Departments.AddDepartment("Collections", "COL");

        var result = await f.Service.CreateAsync(Admin, new CreateUserRequestDto(
            "sara.k", "sara@example.test", "Sara K", false, "Str0ng-Pass!", [Roles.DepartmentEmployee], collections.DepartmentId));

        Assert.Equal(AdminOutcome.Success, result.Outcome);
        var user = result.Value!;
        Assert.Equal("sara.k", user.UserName);
        Assert.Equal([Roles.DepartmentEmployee], user.Roles);
        var membership = Assert.Single(user.Departments);
        Assert.Equal("Collections", membership.Name);
        Assert.True(membership.IsPrimary);
        Assert.True(user.IsActive);
        Assert.Contains(f.Audit.Entries, e => e.Action == "AdminCreateUser" && e.ActorEmployeeId == Admin);
    }

    [Fact]
    public async Task Create_rejects_identity_password_policy_failures_and_unknown_roles_without_touching_state()
    {
        var f = Create();

        var weak = await f.Service.CreateAsync(Admin, new CreateUserRequestDto("x", null, "X", false, "short", [], null));
        Assert.Equal(AdminOutcome.ValidationFailed, weak.Outcome);
        Assert.Contains(weak.Errors!, e => e.Contains("8 characters", StringComparison.Ordinal));

        var badRole = await f.Service.CreateAsync(Admin, new CreateUserRequestDto("y", null, "Y", false, "Str0ng-Pass!", ["Wizard"], null));
        Assert.Equal(AdminOutcome.ValidationFailed, badRole.Outcome);

        Assert.Empty((await f.Service.ListAsync(null, true, 1, 10)).Items);
    }

    [Fact]
    public async Task Deactivate_keeps_the_employee_and_its_history_and_never_deletes()
    {
        var f = Create();
        var id = SeedUser(f, "Omar H", Roles.CsAgent);
        f.Employees.ReferencedByHistory.Add(id);

        var result = await f.Service.SetActivationAsync(Admin, id, new SetActiveRequestDto(false, "Left the company"));

        Assert.Equal(AdminOutcome.Success, result.Outcome);
        Assert.False(result.Value!.IsActive);
        Assert.True(result.Value.HasHistory);
        Assert.NotNull(await f.Employees.GetByIdAsync(id));
        Assert.Contains(await f.Service.ListAsync(null, includeInactive: true, 1, 10) is { } list ? list.Items : [], u => u.EmployeeId == id);
        Assert.DoesNotContain((await f.Service.ListAsync(null, includeInactive: false, 1, 10)).Items, u => u.EmployeeId == id);

        var reactivated = await f.Service.SetActivationAsync(Admin, id, new SetActiveRequestDto(true));
        Assert.True(reactivated.Value!.IsActive);
    }

    [Fact]
    public async Task Last_active_administrator_cannot_be_deactivated_or_stripped_of_the_role()
    {
        var f = Create();
        var onlyAdmin = SeedUser(f, "Root Admin", Roles.SystemAdministrator);

        var deactivate = await f.Service.SetActivationAsync(Admin, onlyAdmin, new SetActiveRequestDto(false));
        Assert.Equal(AdminOutcome.Conflict, deactivate.Outcome);

        var strip = await f.Service.SetRolesAsync(Admin, onlyAdmin, new SetUserRolesRequestDto([Roles.CsManager]));
        Assert.Equal(AdminOutcome.Conflict, strip.Outcome);
        Assert.Equal([Roles.SystemAdministrator], (await f.Service.GetAsync(onlyAdmin))!.Roles);
    }

    [Fact]
    public async Task Roles_are_replaced_with_fixed_roles_only()
    {
        var f = Create();
        SeedUser(f, "Root Admin", Roles.SystemAdministrator);
        var id = SeedUser(f, "Lina M", Roles.CsAgent);

        var result = await f.Service.SetRolesAsync(Admin, id, new SetUserRolesRequestDto([Roles.CsSupervisor, Roles.ReportingUser]));

        Assert.Equal(AdminOutcome.Success, result.Outcome);
        Assert.Equal([Roles.CsSupervisor, Roles.ReportingUser], result.Value!.Roles);
        Assert.Equal(AdminOutcome.ValidationFailed, (await f.Service.SetRolesAsync(Admin, id, new SetUserRolesRequestDto(["Nope"]))).Outcome);
    }

    [Fact]
    public async Task Membership_changes_use_the_existing_assignment_model_and_protect_the_primary()
    {
        var f = Create();
        var id = SeedUser(f, "Nour A", Roles.DepartmentEmployee);
        var collections = f.Departments.AddDepartment("Collections", "COL");
        var registration = f.Departments.AddDepartment("Registration", "REG");
        var inactive = f.Departments.AddDepartment("Legacy", "LEG", isActive: false);

        var first = await f.Service.AddDepartmentAsync(Admin, id, new AddDepartmentMembershipRequestDto(collections.DepartmentId, true));
        Assert.Equal(AdminOutcome.Success, first.Outcome);

        var duplicate = await f.Service.AddDepartmentAsync(Admin, id, new AddDepartmentMembershipRequestDto(collections.DepartmentId, false));
        Assert.Equal(AdminOutcome.Conflict, duplicate.Outcome);

        var toInactive = await f.Service.AddDepartmentAsync(Admin, id, new AddDepartmentMembershipRequestDto(inactive.DepartmentId, false));
        Assert.Equal(AdminOutcome.Conflict, toInactive.Outcome);

        var second = await f.Service.AddDepartmentAsync(Admin, id, new AddDepartmentMembershipRequestDto(registration.DepartmentId, false));
        Assert.Equal(2, second.Value!.Departments.Count);

        var removePrimary = await f.Service.RemoveDepartmentAsync(Admin, id, collections.DepartmentId);
        Assert.Equal(AdminOutcome.Conflict, removePrimary.Outcome);

        var removeSecondary = await f.Service.RemoveDepartmentAsync(Admin, id, registration.DepartmentId);
        Assert.Equal(AdminOutcome.Success, removeSecondary.Outcome);
        Assert.Single(removeSecondary.Value!.Departments);
    }

    [Fact]
    public async Task Search_matches_display_name_user_name_and_email()
    {
        var f = Create();
        SeedUser(f, "Ahmed Saleh", Roles.CsAgent);
        var lina = SeedUser(f, "Lina M", Roles.CsAgent);
        await f.Accounts.UpdateEmailAsync(lina, "lina@tiger.test");

        Assert.Single((await f.Service.ListAsync("ahmed", true, 1, 10)).Items);
        Assert.Single((await f.Service.ListAsync("tiger.test", true, 1, 10)).Items);
        Assert.Equal(2, (await f.Service.ListAsync(null, true, 1, 10)).TotalCount);
        Assert.Equal(AdminOutcome.NotFound, (await f.Service.UpdateProfileAsync(Admin, Guid.NewGuid(), new UpdateUserProfileRequestDto("x", null, false))).Outcome);
    }
}

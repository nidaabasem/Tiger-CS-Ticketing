using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Fakes;

namespace TigerCS.Tests.IdentityAndAccess.Services;

public class UserActivationAppServiceTests
{
    [Fact]
    public async Task SetActivationAsync_DeactivateNonAdmin_Succeeds()
    {
        var employees = new FakeEmployeeRepository();
        var employee = new Employee(Guid.NewGuid(), "J. Smith", isGeynessStaff: false, DateTime.UtcNow);
        employees.Add(employee, Roles.CsAgent);
        var roleReader = new FakeUserRoleReader().Add(employee.EmployeeId, Roles.CsAgent);
        var unitOfWork = new FakeIdentityUnitOfWork();
        var service = new UserActivationAppService(employees, roleReader, unitOfWork, TimeProvider.System);

        var result = await service.SetActivationAsync(employee.EmployeeId, new ActivationRequestDto(false, "Left the company"));

        Assert.Equal(ActivationOutcome.Success, result.Outcome);
        Assert.False(result.Response!.IsActive);
        Assert.NotNull(result.Response.DeactivatedAtUtc);
        Assert.Equal(1, unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task SetActivationAsync_DeactivateLastActiveAdmin_IsRejected()
    {
        var employees = new FakeEmployeeRepository();
        var admin = new Employee(Guid.NewGuid(), "Admin One", isGeynessStaff: false, DateTime.UtcNow);
        employees.Add(admin, Roles.SystemAdministrator);
        var roleReader = new FakeUserRoleReader().Add(admin.EmployeeId, Roles.SystemAdministrator);
        var unitOfWork = new FakeIdentityUnitOfWork();
        var service = new UserActivationAppService(employees, roleReader, unitOfWork, TimeProvider.System);

        var result = await service.SetActivationAsync(admin.EmployeeId, new ActivationRequestDto(false, null));

        Assert.Equal(ActivationOutcome.LastActiveAdministrator, result.Outcome);
        Assert.True(admin.IsActive);
        Assert.Equal(0, unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task SetActivationAsync_DeactivateOneOfTwoAdmins_Succeeds()
    {
        var employees = new FakeEmployeeRepository();
        var adminOne = new Employee(Guid.NewGuid(), "Admin One", isGeynessStaff: false, DateTime.UtcNow);
        var adminTwo = new Employee(Guid.NewGuid(), "Admin Two", isGeynessStaff: false, DateTime.UtcNow);
        employees.Add(adminOne, Roles.SystemAdministrator);
        employees.Add(adminTwo, Roles.SystemAdministrator);
        var roleReader = new FakeUserRoleReader()
            .Add(adminOne.EmployeeId, Roles.SystemAdministrator)
            .Add(adminTwo.EmployeeId, Roles.SystemAdministrator);
        var unitOfWork = new FakeIdentityUnitOfWork();
        var service = new UserActivationAppService(employees, roleReader, unitOfWork, TimeProvider.System);

        var result = await service.SetActivationAsync(adminOne.EmployeeId, new ActivationRequestDto(false, null));

        Assert.Equal(ActivationOutcome.Success, result.Outcome);
        Assert.False(adminOne.IsActive);
    }

    [Fact]
    public async Task SetActivationAsync_UnknownEmployee_ReturnsNotFound()
    {
        var employees = new FakeEmployeeRepository();
        var roleReader = new FakeUserRoleReader();
        var unitOfWork = new FakeIdentityUnitOfWork();
        var service = new UserActivationAppService(employees, roleReader, unitOfWork, TimeProvider.System);

        var result = await service.SetActivationAsync(Guid.NewGuid(), new ActivationRequestDto(false, null));

        Assert.Equal(ActivationOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task SetActivationAsync_ReactivateDeactivatedEmployee_Succeeds()
    {
        var employees = new FakeEmployeeRepository();
        var employee = new Employee(Guid.NewGuid(), "J. Smith", isGeynessStaff: false, DateTime.UtcNow);
        employee.Deactivate(DateTime.UtcNow);
        employees.Add(employee, Roles.CsAgent);
        var roleReader = new FakeUserRoleReader().Add(employee.EmployeeId, Roles.CsAgent);
        var unitOfWork = new FakeIdentityUnitOfWork();
        var service = new UserActivationAppService(employees, roleReader, unitOfWork, TimeProvider.System);

        var result = await service.SetActivationAsync(employee.EmployeeId, new ActivationRequestDto(true, null));

        Assert.Equal(ActivationOutcome.Success, result.Outcome);
        Assert.True(result.Response!.IsActive);
        Assert.Null(result.Response.DeactivatedAtUtc);
    }
}

using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Tests.IdentityAndAccess.Fakes;

namespace TigerCS.Tests.IdentityAndAccess.Services;

/// <summary>
/// <c>GET /api/departments</c>'s app service: the Department directory a
/// Department dropdown reads from, so the New Ticket wizard never asks
/// anyone to type a raw <c>DepartmentId</c>.
/// </summary>
public class DepartmentDirectoryAppServiceTests
{
    private static (DepartmentDirectoryAppService Service, FakeDepartmentRepository Departments) CreateSut()
    {
        var departments = new FakeDepartmentRepository();
        return (new DepartmentDirectoryAppService(departments), departments);
    }

    [Fact]
    public async Task ListAsync_ReturnsRealIdsAndNames_OrderedByName()
    {
        var (service, departments) = CreateSut();
        departments.AddDepartment("Facilities Management", "FM");
        departments.AddDepartment("Customer Service", "CS");

        var result = await service.ListAsync(activeOnly: true);

        Assert.Equal(["Customer Service", "Facilities Management"], result.Select(d => d.Name));
        Assert.All(result, d => Assert.True(d.DepartmentId > 0));
    }

    [Fact]
    public async Task ListAsync_ActiveOnlyTrue_ExcludesDeactivatedDepartments()
    {
        var (service, departments) = CreateSut();
        departments.AddDepartment("Customer Service", "CS", isActive: true);
        departments.AddDepartment("Retired Department", "RD", isActive: false);

        var result = await service.ListAsync(activeOnly: true);

        var single = Assert.Single(result);
        Assert.Equal("Customer Service", single.Name);
    }

    [Fact]
    public async Task ListAsync_ActiveOnlyFalse_IncludesDeactivatedDepartments()
    {
        var (service, departments) = CreateSut();
        departments.AddDepartment("Customer Service", "CS", isActive: true);
        departments.AddDepartment("Retired Department", "RD", isActive: false);

        var result = await service.ListAsync(activeOnly: false);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task ListAsync_NoDepartments_ReturnsEmptyList()
    {
        var (service, _) = CreateSut();

        var result = await service.ListAsync(activeOnly: true);

        Assert.Empty(result);
    }
}

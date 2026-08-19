using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Tests.IdentityAndAccess.Authorization;

public class ActiveEmployeeHandlerTests
{
    private sealed class FakeEmployeeDirectory(bool isActive) : IEmployeeDirectory
    {
        public Task<Employee?> FindByIdAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> IsActiveAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(isActive);

        public Task<IReadOnlyCollection<UserDepartmentAssignment>> GetDepartmentAssignmentsAsync(
            Guid employeeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<UserDepartmentAssignment>>([]);
    }

    private static AuthorizationHandlerContext CreateContext(Guid employeeId, IAuthorizationRequirement requirement)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, employeeId.ToString())], "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        return new AuthorizationHandlerContext([requirement], principal, resource: null);
    }

    [Fact]
    public async Task ActiveEmployee_Succeeds()
    {
        var requirement = new ActiveEmployeeRequirement();
        var context = CreateContext(Guid.NewGuid(), requirement);
        var handler = new ActiveEmployeeHandler(new FakeEmployeeDirectory(isActive: true));

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task DeactivatedEmployee_DoesNotSucceed()
    {
        var requirement = new ActiveEmployeeRequirement();
        var context = CreateContext(Guid.NewGuid(), requirement);
        var handler = new ActiveEmployeeHandler(new FakeEmployeeDirectory(isActive: false));

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }
}

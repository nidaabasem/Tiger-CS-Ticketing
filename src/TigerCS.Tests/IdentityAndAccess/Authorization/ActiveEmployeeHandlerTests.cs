using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Tests.Administration.Services;

namespace TigerCS.Tests.IdentityAndAccess.Authorization;

/// <summary>
/// The per-request session gate: the employee must still be active AND the
/// token's security-stamp claim must still match the account's current
/// stamp — a password reset/change rotates it and so ends every earlier
/// token's life.
/// </summary>
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

    private static AuthorizationHandlerContext CreateContext(Guid employeeId, IAuthorizationRequirement requirement, string? securityStamp)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, employeeId.ToString()) };
        if (securityStamp is not null)
        {
            claims.Add(new Claim(TigerCsTokenClaims.SecurityStamp, securityStamp));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        return new AuthorizationHandlerContext([requirement], principal, resource: null);
    }

    private static (ActiveEmployeeHandler Handler, Guid EmployeeId, string Stamp) Create(bool isActive)
    {
        var employeeId = Guid.NewGuid();
        var accounts = new FakeUserAccountManager().Add(employeeId, "someone");
        return (new ActiveEmployeeHandler(new FakeEmployeeDirectory(isActive), accounts), employeeId, accounts.SecurityStampOf(employeeId));
    }

    [Fact]
    public async Task ActiveEmployee_WithCurrentStamp_Succeeds()
    {
        var (handler, employeeId, stamp) = Create(isActive: true);
        var requirement = new ActiveEmployeeRequirement();
        var context = CreateContext(employeeId, requirement, stamp);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task DeactivatedEmployee_DoesNotSucceed()
    {
        var (handler, employeeId, stamp) = Create(isActive: false);
        var requirement = new ActiveEmployeeRequirement();
        var context = CreateContext(employeeId, requirement, stamp);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task StaleStamp_AfterAPasswordChange_DoesNotSucceed()
    {
        var employeeId = Guid.NewGuid();
        var accounts = new FakeUserAccountManager().Add(employeeId, "someone");
        var stampAtLogin = accounts.SecurityStampOf(employeeId);
        var handler = new ActiveEmployeeHandler(new FakeEmployeeDirectory(isActive: true), accounts);

        // The stamp rotates (an administrative reset here; a self-service change does the same).
        await accounts.ResetPasswordAsync(employeeId, "Another-Pass-2!");
        var context = CreateContext(employeeId, new ActiveEmployeeRequirement(), stampAtLogin);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task TokenWithoutAStampClaim_DoesNotSucceed()
    {
        var (handler, employeeId, _) = Create(isActive: true);
        var context = CreateContext(employeeId, new ActiveEmployeeRequirement(), securityStamp: null);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task UnknownAccount_DoesNotSucceed()
    {
        var accounts = new FakeUserAccountManager();
        var handler = new ActiveEmployeeHandler(new FakeEmployeeDirectory(isActive: true), accounts);
        var context = CreateContext(Guid.NewGuid(), new ActiveEmployeeRequirement(), "any-stamp");

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }
}

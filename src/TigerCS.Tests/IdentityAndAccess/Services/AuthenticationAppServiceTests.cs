using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Tests.IdentityAndAccess.Fakes;

namespace TigerCS.Tests.IdentityAndAccess.Services;

public class AuthenticationAppServiceTests
{
    private sealed class FakeAuthenticator(CredentialCheckResult result) : IIdentityAuthenticator
    {
        public Task<CredentialCheckResult> CheckCredentialsAsync(
            string username, string password, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class FakeTokenService : ITokenService
    {
        public IssuedToken CreateAccessToken(Guid employeeId, string displayName, IReadOnlyCollection<string> roles) =>
            new("fake-jwt", DateTime.UtcNow.AddHours(1));
    }

    [Fact]
    public async Task LoginAsync_ValidCredentials_ReturnsSuccessWithToken()
    {
        var employeeId = Guid.NewGuid();
        var authenticator = new FakeAuthenticator(
            CredentialCheckResult.Success(employeeId, "J. Smith", ["CS Agent"]));
        var assignments = new FakeUserDepartmentAssignmentRepository();
        var service = new AuthenticationAppService(authenticator, new FakeTokenService(), assignments);

        var result = await service.LoginAsync(new LoginRequestDto("j.smith", "correct-password"));

        Assert.Equal(LoginOutcome.Success, result.Outcome);
        Assert.NotNull(result.Response);
        Assert.Equal(employeeId, result.Response!.EmployeeId);
        Assert.Equal("fake-jwt", result.Response.AccessToken);
        Assert.Contains("CS Agent", result.Response.Roles);
    }

    [Fact]
    public async Task LoginAsync_InvalidCredentials_ReturnsInvalidCredentialsWithoutRevealingWhy()
    {
        var authenticator = new FakeAuthenticator(CredentialCheckResult.InvalidCredentials());
        var service = new AuthenticationAppService(
            authenticator, new FakeTokenService(), new FakeUserDepartmentAssignmentRepository());

        var result = await service.LoginAsync(new LoginRequestDto("unknown", "wrong"));

        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task LoginAsync_LockedAccount_ReturnsLocked()
    {
        var authenticator = new FakeAuthenticator(CredentialCheckResult.Locked());
        var service = new AuthenticationAppService(
            authenticator, new FakeTokenService(), new FakeUserDepartmentAssignmentRepository());

        var result = await service.LoginAsync(new LoginRequestDto("j.smith", "whatever"));

        Assert.Equal(LoginOutcome.Locked, result.Outcome);
        Assert.Null(result.Response);
    }
}

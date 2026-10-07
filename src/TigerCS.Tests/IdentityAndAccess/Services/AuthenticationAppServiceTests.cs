using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Tests.Administration.Services;
using TigerCS.Tests.CustomerVerification.Fakes;
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
        public string? LastSecurityStamp { get; private set; }

        public IssuedToken CreateAccessToken(Guid employeeId, string displayName, IReadOnlyCollection<string> roles, string securityStamp)
        {
            LastSecurityStamp = securityStamp;
            return new("fake-jwt", DateTime.UtcNow.AddHours(1));
        }
    }

    private sealed record Fixture(
        AuthenticationAppService Service,
        FakeTokenService Tokens,
        FakeUserAccountManager Accounts,
        FakeIdentityUnitOfWork UnitOfWork,
        FakeAuditEntryWriter Audit);

    private static Fixture Create(CredentialCheckResult? credentialCheck = null, FakeUserAccountManager? accounts = null)
    {
        var tokens = new FakeTokenService();
        accounts ??= new FakeUserAccountManager();
        var unitOfWork = new FakeIdentityUnitOfWork();
        var audit = new FakeAuditEntryWriter();
        var service = new AuthenticationAppService(
            new FakeAuthenticator(credentialCheck ?? CredentialCheckResult.InvalidCredentials()),
            tokens, new FakeUserDepartmentAssignmentRepository(), accounts, unitOfWork, audit);
        return new Fixture(service, tokens, accounts, unitOfWork, audit);
    }

    [Fact]
    public async Task LoginAsync_ValidCredentials_ReturnsSuccessWithToken()
    {
        var employeeId = Guid.NewGuid();
        var f = Create(CredentialCheckResult.Success(employeeId, "J. Smith", ["CS Agent"], "stamp-1"));

        var result = await f.Service.LoginAsync(new LoginRequestDto("j.smith", "correct-password"));

        Assert.Equal(LoginOutcome.Success, result.Outcome);
        Assert.NotNull(result.Response);
        Assert.Equal(employeeId, result.Response!.EmployeeId);
        Assert.Equal("fake-jwt", result.Response.AccessToken);
        Assert.Contains("CS Agent", result.Response.Roles);
        // The token is bound to the stamp the credential check reported.
        Assert.Equal("stamp-1", f.Tokens.LastSecurityStamp);
    }

    [Fact]
    public async Task LoginAsync_InvalidCredentials_ReturnsInvalidCredentialsWithoutRevealingWhy()
    {
        var f = Create(CredentialCheckResult.InvalidCredentials());

        var result = await f.Service.LoginAsync(new LoginRequestDto("unknown", "wrong"));

        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task LoginAsync_LockedAccount_ReturnsLocked()
    {
        var f = Create(CredentialCheckResult.Locked());

        var result = await f.Service.LoginAsync(new LoginRequestDto("j.smith", "whatever"));

        Assert.Equal(LoginOutcome.Locked, result.Outcome);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task ChangePasswordAsync_CorrectCurrentPassword_ChangesIt_RotatesTheStamp_AndAuditsWithoutSecrets()
    {
        var employeeId = Guid.NewGuid();
        var accounts = new FakeUserAccountManager().Add(employeeId, "j.smith").WithPassword(employeeId, "Old-Pass-1!");
        var stampBefore = accounts.SecurityStampOf(employeeId);
        var f = Create(accounts: accounts);

        var result = await f.Service.ChangePasswordAsync(employeeId, new ChangePasswordRequestDto("Old-Pass-1!", "New-Pass-2!"));

        Assert.Equal(ChangePasswordOutcome.Success, result.Outcome);
        Assert.Equal("New-Pass-2!", accounts.PasswordOf(employeeId));
        Assert.NotEqual(stampBefore, accounts.SecurityStampOf(employeeId));
        Assert.Equal(1, f.UnitOfWork.SaveChangesCallCount);

        var entry = Assert.Single(f.Audit.Entries);
        Assert.Equal("ChangeOwnPassword", entry.Action);
        Assert.Equal("User", entry.EntityType);
        Assert.Equal(employeeId.ToString(), entry.EntityId);
        Assert.Equal(employeeId, entry.ActorEmployeeId);
        Assert.DoesNotContain("Old-Pass-1!", entry.AfterValue ?? string.Empty);
        Assert.DoesNotContain("New-Pass-2!", entry.AfterValue ?? string.Empty);
        Assert.Contains("SecurityStampRotated=true", entry.AfterValue);
    }

    [Fact]
    public async Task ChangePasswordAsync_WrongCurrentPassword_IsReportedAsExactlyThat_AndChangesNothing()
    {
        var employeeId = Guid.NewGuid();
        var accounts = new FakeUserAccountManager().Add(employeeId, "j.smith").WithPassword(employeeId, "Old-Pass-1!");
        var stampBefore = accounts.SecurityStampOf(employeeId);
        var f = Create(accounts: accounts);

        var result = await f.Service.ChangePasswordAsync(employeeId, new ChangePasswordRequestDto("not-it", "New-Pass-2!"));

        Assert.Equal(ChangePasswordOutcome.CurrentPasswordIncorrect, result.Outcome);
        Assert.Null(result.Errors);
        Assert.Equal("Old-Pass-1!", accounts.PasswordOf(employeeId));
        Assert.Equal(stampBefore, accounts.SecurityStampOf(employeeId));
        Assert.Empty(f.Audit.Entries);
        Assert.Equal(0, f.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ChangePasswordAsync_PolicyFailure_ReturnsIdentitysReasons()
    {
        var employeeId = Guid.NewGuid();
        var accounts = new FakeUserAccountManager().Add(employeeId, "j.smith").WithPassword(employeeId, "Old-Pass-1!");
        var f = Create(accounts: accounts);

        var result = await f.Service.ChangePasswordAsync(employeeId, new ChangePasswordRequestDto("Old-Pass-1!", "short"));

        Assert.Equal(ChangePasswordOutcome.PasswordPolicyViolation, result.Outcome);
        Assert.Contains("Passwords must be at least 8 characters.", result.Errors!);
        Assert.Equal("Old-Pass-1!", accounts.PasswordOf(employeeId));
        Assert.Empty(f.Audit.Entries);
    }

    [Fact]
    public async Task ChangePasswordAsync_UnknownCaller_ReturnsNotFound()
    {
        var f = Create();

        var result = await f.Service.ChangePasswordAsync(Guid.NewGuid(), new ChangePasswordRequestDto("x", "New-Pass-2!"));

        Assert.Equal(ChangePasswordOutcome.NotFound, result.Outcome);
    }
}

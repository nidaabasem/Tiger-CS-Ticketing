using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Fakes;

namespace TigerCS.Tests.IdentityAndAccess.Services;

/// <summary>
/// <see cref="IdentityAuthenticator"/> against the real ASP.NET Core Identity
/// stack (UserManager, SignInManager, PasswordHasher, lockout) over an
/// InMemory store — nothing about password verification is faked. Covers the
/// UAT→Production user migration shape: the password hash is copied verbatim
/// onto an account whose user name is an employee ID, and the person signs in
/// with their email address.
/// </summary>
public class IdentityAuthenticatorTests
{
    private const string Password = "Correct-Horse-1!";
    private const string Email = "fa.aboshanap@tigergroup.ae";
    private const string EmployeeUserName = "FA-0042";

    private sealed class Fixture : IDisposable
    {
        public ServiceProvider Provider { get; }
        public FakeEmployeeRepository Employees { get; } = new();

        public Fixture()
        {
            var name = Guid.NewGuid().ToString("N");
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpContextAccessor();
            services.AddAuthentication();
            services.AddDbContext<TigerCsDbContext>(o => o
                .UseInMemoryDatabase(name)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddIdentityCore<ApplicationUser>(o =>
                {
                    // Same lockout policy as InfrastructureServiceCollectionExtensions.
                    o.Lockout.MaxFailedAccessAttempts = 5;
                    o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                    o.Lockout.AllowedForNewUsers = true;
                    o.User.RequireUniqueEmail = false;
                })
                .AddRoles<ApplicationRole>()
                .AddEntityFrameworkStores<TigerCsDbContext>()
                .AddSignInManager();
            services.AddScoped<IEmployeeRepository>(_ => Employees);
            services.AddScoped<IdentityAuthenticator>();
            Provider = services.BuildServiceProvider();
        }

        public IdentityAuthenticator Authenticator(IServiceScope scope)
        {
            // SignInManager needs an HttpContext only for cookie sign-in, which CheckPasswordSignInAsync never touches.
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext();
            return scope.ServiceProvider.GetRequiredService<IdentityAuthenticator>();
        }

        public async Task<Guid> SeedAsync(
            string userName, string? email, string? passwordHash = null, bool active = true)
        {
            using var scope = Provider.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = userName, Email = email };
            var result = passwordHash is null
                ? await users.CreateAsync(user, Password)
                : await users.CreateAsync(user);
            Assert.True(result.Succeeded);
            if (passwordHash is not null)
            {
                // The migration shape: the existing hash is carried over as-is.
                user.PasswordHash = passwordHash;
                Assert.True((await users.UpdateAsync(user)).Succeeded);
            }

            var employee = new Employee(user.Id, "Test Employee", isGeynessStaff: false, DateTime.UtcNow);
            if (!active)
            {
                employee.Deactivate(DateTime.UtcNow);
            }

            Employees.Add(employee);
            return user.Id;
        }

        public async Task<string> HashOfAsync(Guid id)
        {
            using var scope = Provider.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            return (await users.FindByIdAsync(id.ToString()))!.PasswordHash!;
        }

        public async Task<CredentialCheckResult> LoginAsync(string identifier, string password)
        {
            using var scope = Provider.CreateScope();
            return await Authenticator(scope).CheckCredentialsAsync(identifier, password);
        }

        public void Dispose() => Provider.Dispose();
    }

    /// <summary>A "UAT" account whose hash is then copied onto a differently-named "Production" account.</summary>
    private static async Task<string> UatHashAsync()
    {
        using var uat = new Fixture();
        var id = await uat.SeedAsync("uat-account", "uat@tigergroup.ae");
        return await uat.HashOfAsync(id);
    }

    [Fact]
    public async Task MigratedUser_SignsInWithEmployeeId_AndWithEmail_UsingCopiedHash()
    {
        var hash = await UatHashAsync();
        using var f = new Fixture();
        var id = await f.SeedAsync(EmployeeUserName, Email, hash);

        var byId = await f.LoginAsync(EmployeeUserName, Password);
        var byEmail = await f.LoginAsync(Email, Password);
        var byEmailOtherCase = await f.LoginAsync("  FA.Aboshanap@TigerGroup.ae ", Password);

        foreach (var r in new[] { byId, byEmail, byEmailOtherCase })
        {
            Assert.Equal(CredentialCheckOutcome.Success, r.Outcome);
            Assert.Equal(id, r.EmployeeId);
            Assert.False(string.IsNullOrEmpty(r.SecurityStamp));
        }
    }

    [Fact]
    public async Task MigratedUser_WithUserNameEqualToEmail_StillSignsIn()
    {
        var hash = await UatHashAsync();
        using var f = new Fixture();
        await f.SeedAsync("support@tigergroup.ae", "support@tigergroup.ae", hash);

        var result = await f.LoginAsync("support@tigergroup.ae", Password);

        Assert.Equal(CredentialCheckOutcome.Success, result.Outcome);
    }

    [Theory]
    [InlineData(EmployeeUserName)]
    [InlineData(Email)]
    public async Task WrongPassword_IsInvalidCredentials(string identifier)
    {
        using var f = new Fixture();
        await f.SeedAsync(EmployeeUserName, Email);

        var result = await f.LoginAsync(identifier, "Wrong-Password-9!");

        Assert.Equal(CredentialCheckOutcome.InvalidCredentials, result.Outcome);
    }

    [Fact]
    public async Task UnknownIdentifier_IsInvalidCredentials_SameAsWrongPassword()
    {
        using var f = new Fixture();
        await f.SeedAsync(EmployeeUserName, Email);

        Assert.Equal(CredentialCheckOutcome.InvalidCredentials, (await f.LoginAsync("nobody@tigergroup.ae", Password)).Outcome);
        Assert.Equal(CredentialCheckOutcome.InvalidCredentials, (await f.LoginAsync("NOBODY", Password)).Outcome);
    }

    [Fact]
    public async Task EmailSharedByTwoAccounts_IsAmbiguous_AndRejectedRatherThanGuessed()
    {
        using var f = new Fixture();
        await f.SeedAsync("FA-0001", Email);
        await f.SeedAsync("FA-0002", Email);

        var result = await f.LoginAsync(Email, Password);

        Assert.Equal(CredentialCheckOutcome.InvalidCredentials, result.Outcome);
    }

    [Theory]
    [InlineData(EmployeeUserName)]
    [InlineData(Email)]
    public async Task InactiveEmployee_WithCorrectPassword_IsGenericInvalidCredentials(string identifier)
    {
        using var f = new Fixture();
        await f.SeedAsync(EmployeeUserName, Email, active: false);

        var result = await f.LoginAsync(identifier, Password);

        Assert.Equal(CredentialCheckOutcome.InvalidCredentials, result.Outcome);
    }

    [Fact]
    public async Task RepeatedFailures_LockTheAccount_EvenForTheCorrectPassword_AndByEitherIdentifier()
    {
        using var f = new Fixture();
        await f.SeedAsync(EmployeeUserName, Email);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(CredentialCheckOutcome.InvalidCredentials, (await f.LoginAsync(Email, "Wrong-Password-9!")).Outcome);
        }

        // The fifth failure (by email) trips the lockout.
        Assert.Equal(CredentialCheckOutcome.Locked, (await f.LoginAsync(Email, "Wrong-Password-9!")).Outcome);
        // Correct password no longer gets through, through either identifier.
        Assert.Equal(CredentialCheckOutcome.Locked, (await f.LoginAsync(Email, Password)).Outcome);
        Assert.Equal(CredentialCheckOutcome.Locked, (await f.LoginAsync(EmployeeUserName, Password)).Outcome);
    }

    [Fact]
    public async Task SuccessfulLogin_DoesNotModifyTheStoredUserRecord()
    {
        var hash = await UatHashAsync();
        using var f = new Fixture();
        var id = await f.SeedAsync(EmployeeUserName, Email, hash);
        var before = await f.HashOfAsync(id);

        await f.LoginAsync(Email, Password);

        Assert.Equal(before, await f.HashOfAsync(id));
    }
}

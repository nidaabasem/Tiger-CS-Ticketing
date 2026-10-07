using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Identity;

namespace TigerCS.Tests.IdentityAndAccess.Integration;

/// <summary>
/// Password management end to end through the real Api host: the
/// administrative reset and the self-service change, Identity's own policy
/// deciding what is acceptable, the audit trail carrying no secret, and —
/// the point of the security-stamp claim — every token issued before the
/// change being refused afterwards while a fresh sign-in works.
/// </summary>
public class PasswordManagementEndpointsTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public PasswordManagementEndpointsTests(TigerCsApiFactory factory) => _factory = factory;

    private async Task<HttpClient> SignInAsync(string username, string password)
    {
        var client = _factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    private async Task<HttpStatusCode> LoginStatusAsync(string username, string password)
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        return response.StatusCode;
    }

    private static async Task<JsonElement> ProblemOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement;
    }

    private static string ErrorsOf(JsonElement problem) =>
        string.Join(" ", problem.GetProperty("errors").EnumerateObject()
            .SelectMany(p => p.Value.EnumerateArray().Select(e => e.GetString())));

    // ---------------------------------------------------------- admin reset

    [Fact]
    public async Task AdminReset_WeakPassword_Returns422WithIdentitysReasons_AndChangesNothing()
    {
        var (adminName, adminPassword, _) = await _factory.SeedEmployeeAsync(Roles.SystemAdministrator);
        var admin = await SignInAsync(adminName, adminPassword);
        var (targetName, targetPassword, targetId) = await _factory.SeedEmployeeAsync(Roles.CsAgent);

        var response = await admin.PostAsJsonAsync($"/api/admin/users/{targetId}/password",
            new ResetUserPasswordRequestDto("short", "typo"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ProblemOf(response);
        Assert.Equal("https://tigercs.internal/problems/password-policy-violation", problem.GetProperty("type").GetString());
        var errors = ErrorsOf(problem);
        Assert.Contains("at least 8 characters", errors);
        Assert.Contains("uppercase", errors);
        Assert.DoesNotContain("short", errors);

        // The old password still works; nothing was audited.
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(targetName, targetPassword));
        Assert.DoesNotContain(await _factory.GetAuditEntriesAsync(targetId.ToString()), a => a.Action == "AdminResetUserPassword");
    }

    [Fact]
    public async Task AdminReset_GoodPassword_Returns204_InvalidatesEveryEarlierToken_LiftsLockout_AndAuditsWithoutTheSecret()
    {
        var (adminName, adminPassword, _) = await _factory.SeedEmployeeAsync(Roles.SystemAdministrator);
        var admin = await SignInAsync(adminName, adminPassword);
        var (targetName, targetPassword, targetId) = await _factory.SeedEmployeeAsync(Roles.CsAgent);

        // The target is signed in and locked out (too many failed attempts).
        var targetBefore = await SignInAsync(targetName, targetPassword);
        Assert.Equal(HttpStatusCode.OK, (await targetBefore.GetAsync("/api/users/me")).StatusCode);
        await LockOutAsync(targetId);
        Assert.Equal(HttpStatusCode.Locked, await LoginStatusAsync(targetName, targetPassword));

        var response = await admin.PostAsJsonAsync($"/api/admin/users/{targetId}/password",
            new ResetUserPasswordRequestDto("Brand-New-Pass-7!", "Forgot password at the desk"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The token issued before the reset — unexpired, never reissued — is refused now.
        Assert.Equal(HttpStatusCode.Forbidden, (await targetBefore.GetAsync("/api/users/me")).StatusCode);

        // The old password is gone, the lockout is lifted, and a fresh sign-in works.
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginStatusAsync(targetName, targetPassword));
        var targetAfter = await SignInAsync(targetName, "Brand-New-Pass-7!");
        Assert.Equal(HttpStatusCode.OK, (await targetAfter.GetAsync("/api/users/me")).StatusCode);

        var entry = Assert.Single(await _factory.GetAuditEntriesAsync(targetId.ToString()), a => a.Action == "AdminResetUserPassword");
        Assert.Equal("User", entry.EntityType);
        Assert.Equal("Reason=Forgot password at the desk;LockoutCleared=true;SecurityStampRotated=true", entry.AfterValue);
        Assert.DoesNotContain("Brand-New-Pass-7!", entry.AfterValue ?? string.Empty);
    }

    [Fact]
    public async Task AdminReset_UnknownUser_Returns404_AndNonAdministrator_Returns403()
    {
        var (adminName, adminPassword, _) = await _factory.SeedEmployeeAsync(Roles.SystemAdministrator);
        var admin = await SignInAsync(adminName, adminPassword);
        var (agentName, agentPassword, _) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        var agent = await SignInAsync(agentName, agentPassword);
        var (_, _, targetId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);

        var unknown = await admin.PostAsJsonAsync($"/api/admin/users/{Guid.NewGuid()}/password",
            new ResetUserPasswordRequestDto("Brand-New-Pass-7!", null));
        var forbidden = await agent.PostAsJsonAsync($"/api/admin/users/{targetId}/password",
            new ResetUserPasswordRequestDto("Brand-New-Pass-7!", null));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    // ------------------------------------------------------- change my own

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_Returns422SayingOnlyThat()
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        var client = await SignInAsync(username, password);

        var response = await client.PostAsJsonAsync("/api/auth/change-password",
            new ChangePasswordRequestDto("not-my-password", "Brand-New-Pass-7!"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ProblemOf(response);
        Assert.Equal("https://tigercs.internal/problems/current-password-incorrect", problem.GetProperty("type").GetString());
        Assert.Equal("The current password is incorrect.", ErrorsOf(problem));

        // Nothing changed: the session still works, the old password still signs in.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(username, password));
        Assert.DoesNotContain(await _factory.GetAuditEntriesAsync(employeeId.ToString()), a => a.Action == "ChangeOwnPassword");
    }

    [Fact]
    public async Task ChangePassword_WeakNewPassword_Returns422WithIdentitysReasons()
    {
        var (username, password, _) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        var client = await SignInAsync(username, password);

        var response = await client.PostAsJsonAsync("/api/auth/change-password",
            new ChangePasswordRequestDto(password, "alllowercase"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ProblemOf(response);
        Assert.Equal("https://tigercs.internal/problems/password-policy-violation", problem.GetProperty("type").GetString());
        Assert.Contains("uppercase", ErrorsOf(problem));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users/me")).StatusCode);
    }

    [Fact]
    public async Task ChangePassword_BlankFields_Returns400()
    {
        var (username, password, _) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        var client = await SignInAsync(username, password);

        var response = await client.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequestDto("", ""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_Success_Returns204_RejectsTheCallersOwnTokenAfterwards_AndAFreshLoginWorks()
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        var client = await SignInAsync(username, password);
        var otherDevice = await SignInAsync(username, password);

        var response = await client.PostAsJsonAsync("/api/auth/change-password",
            new ChangePasswordRequestDto(password, "Brand-New-Pass-7!"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Both pre-change tokens die at once — the caller's and the other device's.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherDevice.GetAsync("/api/users/me")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await LoginStatusAsync(username, password));
        var fresh = await SignInAsync(username, "Brand-New-Pass-7!");
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/users/me")).StatusCode);

        var entry = Assert.Single(await _factory.GetAuditEntriesAsync(employeeId.ToString()), a => a.Action == "ChangeOwnPassword");
        Assert.Equal("User", entry.EntityType);
        Assert.Equal(employeeId, entry.ActorEmployeeId);
        Assert.Equal("SecurityStampRotated=true", entry.AfterValue);
        Assert.DoesNotContain(password, entry.AfterValue ?? string.Empty);
    }

    [Fact]
    public async Task ChangePassword_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/change-password",
            new ChangePasswordRequestDto("Test-Password-1!", "Brand-New-Pass-7!"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ------------------------------------------------------------- helpers

    /// <summary>Puts the account in Identity's locked-out state, as too many failed attempts would.</summary>
    private async Task LockOutAsync(Guid employeeId)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(employeeId.ToString());
        await userManager.SetLockoutEnabledAsync(user!, true);
        await userManager.SetLockoutEndDateAsync(user!, DateTimeOffset.UtcNow.AddHours(1));
    }
}

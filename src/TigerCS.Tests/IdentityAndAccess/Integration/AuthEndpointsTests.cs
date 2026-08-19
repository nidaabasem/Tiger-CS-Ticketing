using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Tests.IdentityAndAccess.Integration;

public class AuthEndpointsTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public AuthEndpointsTests(TigerCsApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetUsersMe_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/users/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetRoles_AsCsAgent_Returns403InsufficientPermissions()
    {
        var (username, password, _) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        var rolesResponse = await client.GetAsync("/api/roles");

        Assert.Equal(HttpStatusCode.Forbidden, rolesResponse.StatusCode);
    }

    [Fact]
    public async Task GetRoles_AsSystemAdministrator_Returns200()
    {
        var (username, password, _) = await _factory.SeedEmployeeAsync(Roles.SystemAdministrator);
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        var rolesResponse = await client.GetAsync("/api/roles");

        Assert.Equal(HttpStatusCode.OK, rolesResponse.StatusCode);
        var roles = await rolesResponse.Content.ReadFromJsonAsync<List<RoleDto>>();
        Assert.NotNull(roles);
        Assert.Equal(Roles.All.Count, roles!.Count);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401InvalidCredentials()
    {
        var (username, _, _) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, "wrong-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_UnknownUsername_Returns401SameAsWrongPassword()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequestDto($"no-such-user-{Guid.NewGuid():N}", "whatever"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetUsersMe_WithValidToken_Returns200()
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        var meResponse = await client.GetAsync("/api/users/me");

        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        var me = await meResponse.Content.ReadFromJsonAsync<CurrentUserResponseDto>();
        Assert.Equal(employeeId, me!.EmployeeId);
        Assert.Contains(Roles.CsAgent, me.Roles);
    }

    [Fact]
    public async Task Login_DeactivatedEmployee_Returns401SameAsInvalidCredentials()
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(Roles.CsAgent);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var employee = await db.Employees.FindAsync(employeeId);
            employee!.Deactivate(DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_RepeatedFailures_LocksAccount()
    {
        var (username, _, _) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        var client = _factory.CreateClient();

        HttpResponseMessage? last = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            last = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, "wrong-password"));
        }

        Assert.Equal((HttpStatusCode)423, last!.StatusCode);
    }

    /// <summary>
    /// Review item 3: a JWT issued before deactivation must stop working
    /// immediately on every protected endpoint — not just ones with a
    /// tiered policy. Checked against both a tiered-policy endpoint
    /// (/api/roles) and a plain-FallbackPolicy endpoint (/api/users/me) to
    /// prove the ActiveEmployeeRequirement is enforced globally, not only
    /// on selected policies.
    ///
    /// Expected status is 403, not 401: the JWT itself is still a valid
    /// credential (correct signature, not expired) — authentication
    /// succeeds. It's authorization (ActiveEmployeeRequirement) that
    /// correctly denies the request, and ASP.NET Core's authorization
    /// middleware maps an authenticated-but-not-authorized result to 403,
    /// reserving 401 for missing/invalid credentials (see
    /// GetUsersMe_WithoutToken_Returns401, a true 401 case).
    /// </summary>
    [Fact]
    public async Task Token_ReusedAfterDeactivation_IsRejectedOnEveryProtectedEndpoint()
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(Roles.SystemAdministrator);
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        // Prove the token works before deactivation.
        var meBefore = await client.GetAsync("/api/users/me");
        Assert.Equal(HttpStatusCode.OK, meBefore.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var employee = await db.Employees.FindAsync(employeeId);
            employee!.Deactivate(DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        // Same still-unexpired, never-reissued token — must now be rejected
        // everywhere, without logging in again.
        var meAfter = await client.GetAsync("/api/users/me");
        var rolesAfter = await client.GetAsync("/api/roles");

        Assert.Equal(HttpStatusCode.Forbidden, meAfter.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, rolesAfter.StatusCode);
    }

    [Fact]
    public async Task SetActivation_AsNonAdmin_Returns403()
    {
        var (username, password, _) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        var (_, _, targetEmployeeId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        var response = await client.PatchAsJsonAsync(
            $"/api/users/{targetEmployeeId}/activation", new ActivationRequestDto(false, "test"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SetActivation_LastActiveAdministrator_ReturnsConflict()
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(Roles.SystemAdministrator);

        // This test class shares one database across all tests (IClassFixture),
        // and other tests also seed System Administrators — so "last active
        // admin" has to be established explicitly here, not assumed from a
        // global count. Deactivating every other currently-active admin makes
        // this employee provably the last one, regardless of run order.
        await DeactivateAllOtherAdministratorsAsync(employeeId);

        var client = _factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        // Self-deactivation is not specially blocked (see the success case
        // below) — the last-admin guard is what must still apply here.
        var response = await client.PatchAsJsonAsync(
            $"/api/users/{employeeId}/activation", new ActivationRequestDto(false, "test"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task SetActivation_SelfDeactivation_WhenAnotherAdminIsActive_Succeeds()
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(Roles.SystemAdministrator);
        await _factory.SeedEmployeeAsync(Roles.SystemAdministrator); // a second active admin
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        var response = await client.PatchAsJsonAsync(
            $"/api/users/{employeeId}/activation", new ActivationRequestDto(false, "self-deactivation test"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<ActivationResponseDto>();
        Assert.False(updated!.IsActive);
    }

    private async Task DeactivateAllOtherAdministratorsAsync(Guid exceptEmployeeId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admins = await userManager.GetUsersInRoleAsync(Roles.SystemAdministrator);

        foreach (var admin in admins.Where(a => a.Id != exceptEmployeeId))
        {
            var employee = await db.Employees.FindAsync(admin.Id);
            if (employee is { IsActive: true })
            {
                employee.Deactivate(DateTime.UtcNow);
            }
        }

        await db.SaveChangesAsync();
    }
}

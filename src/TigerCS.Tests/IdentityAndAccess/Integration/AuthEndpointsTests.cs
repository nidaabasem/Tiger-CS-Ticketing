using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
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
}

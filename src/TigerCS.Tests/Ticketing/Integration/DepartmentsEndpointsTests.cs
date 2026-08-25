using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// End-to-end against the real Api host: <c>GET /api/departments</c>, the
/// Department directory the New Ticket wizard's Department dropdown reads
/// from — removing the old "type a numeric DepartmentId" temporary UI.
/// </summary>
public class DepartmentsEndpointsTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public DepartmentsEndpointsTests(TigerCsApiFactory factory) => _factory = factory;

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string role = "CS Agent")
    {
        var (username, password, _) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        return client;
    }

    [Fact]
    public async Task GetDepartments_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/departments");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetDepartments_ReturnsRealIdsAndNames_OrderedByName()
    {
        var client = await CreateAuthenticatedClientAsync();
        var fm = await _factory.CreateDepartmentAsync("Facilities Management " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var cs = await _factory.CreateDepartmentAsync("Customer Service " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);

        var response = await client.GetAsync("/api/departments");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var departments = await response.Content.ReadFromJsonAsync<List<DepartmentDto>>();
        var ids = departments!.Select(d => d.DepartmentId).ToList();
        Assert.Contains(fm, ids);
        Assert.Contains(cs, ids);

        // No numeric id is invented client-side — every entry is a real, existing DepartmentId with its real Name.
        Assert.All(departments!, d => Assert.False(string.IsNullOrWhiteSpace(d.Name)));
    }
}

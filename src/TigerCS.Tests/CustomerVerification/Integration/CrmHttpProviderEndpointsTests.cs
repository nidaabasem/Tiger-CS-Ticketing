using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.CustomerVerification.Integration;

/// <summary>
/// The real environments' standard <c>Crm:Provider = "Http"</c>, against the
/// real Api host: the two ports Tiger CRM publishes no endpoint for answer
/// with their outage contract (never MockCrmGateway fixture data), while the
/// real CRM Buyer Lookup and every endpoint whose controller merely
/// constructs those ports keep working.
/// </summary>
public sealed class CrmHttpProviderEndpointsTests : IDisposable
{
    private readonly TigerCsApiFactory _factory = new() { CrmProvider = "Http" };

    public void Dispose() => _factory.Dispose();

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

    private static async Task AssertCrmUnavailableProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("crm-unavailable", problem.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task GetUnit_WithHttpProvider_Returns502_NeverTheFixtureUnit()
    {
        var client = await CreateAuthenticatedClientAsync();

        // CRM-UNIT-1001 is MockCrmGateway's best-known fixture: a 200 here
        // would mean a real environment is being served fake data.
        var response = await client.GetAsync("/api/crm/units/CRM-UNIT-1001");

        await AssertCrmUnavailableProblemAsync(response);
    }

    [Fact]
    public async Task SearchUnits_WithHttpProvider_Returns502()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/crm/units/search?unitNumber=1204");

        await AssertCrmUnavailableProblemAsync(response);
    }

    [Fact]
    public async Task GetContacts_WithHttpProvider_Returns502()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/crm/units/CRM-UNIT-1001/contacts");

        await AssertCrmUnavailableProblemAsync(response);
    }

    [Fact]
    public async Task GetBuyerByPhone_WithHttpProvider_StillReturns200_TheRealPortIsUntouched()
    {
        var client = await CreateAuthenticatedClientAsync();

        // Same controller as the unit endpoints above: proves the provider
        // switch fails closed per call, never at controller construction.
        var response = await client.GetAsync("/api/crm/buyers?phoneNumber=%2B971500000900");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CustomerLookup_WithHttpProvider_ReportsTheCrmSourceFailed_WhileTheOtherSourcesStillAnswer()
    {
        var client = await CreateAuthenticatedClientAsync();
        var createResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971509990001", null, true, "1204", null));
        createResponse.EnsureSuccessStatusCode();
        var intake = await createResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        // No department on the intake: every configured source is searched.
        // The CRM leg fails closed (never MockCrmGateway's fixture match for
        // this phone); the other legs are unaffected by it.
        var response = await client.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lookup = await response.Content.ReadFromJsonAsync<CustomerLookupResultDto>();
        var crm = Assert.Single(lookup!.Sources, s => s.Source == "Crm");
        Assert.Equal("Failed", crm.Status);
        Assert.Empty(crm.Customers);
        var others = lookup.Sources.Where(s => s.Source != "Crm").ToList();
        Assert.NotEmpty(others);
        Assert.All(others, s => Assert.NotEqual("Failed", s.Status));
    }

    [Fact]
    public async Task SearchCustomers_WithHttpProvider_StillReturns200()
    {
        var client = await CreateAuthenticatedClientAsync();

        // CustomerHistoryController constructs CustomerLookupAppService (and
        // with it both generic CRM ports) for every request.
        var response = await client.GetAsync("/api/customers/search?phoneNumber=%2B971500000900");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

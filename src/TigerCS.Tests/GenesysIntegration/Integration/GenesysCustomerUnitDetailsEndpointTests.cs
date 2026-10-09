using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TigerCS.Api.Controllers;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.GenesysIntegration.Integration;

/// <summary>
/// <c>POST /api/genesys/customers/unit-details</c> through the real host: the
/// Genesys service-account authentication, the server-side ownership check,
/// the error contract, and the exact JSON a Data Action receives. The host's
/// CRM double holds customer 9001 with one unit, 9200; the "Mock" details
/// provider records unit-level handover on 9200.
/// </summary>
public sealed class GenesysCustomerUnitDetailsEndpointTests : IDisposable
{
    private const string Route = "/api/genesys/customers/unit-details";
    private const string Phone = "tel:+971500000900";

    private readonly TigerCsApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> SignInAsync(string role = Roles.CsAgent) => (await SignInWithIdAsync(role)).Client;

    private async Task<(HttpClient Client, Guid EmployeeId)> SignInWithIdAsync(string role = Roles.CsAgent)
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return (client, employeeId);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task NoUnit_ReturnsTheEligibleUnits()
    {
        var client = await SignInAsync();

        var response = await client.PostAsJsonAsync(Route, new GenesysCustomerUnitDetailsRequest("crm:9001", Phone));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("UnitSelectionRequired", body.GetProperty("mode").GetString());
        Assert.Equal("crm:9001", body.GetProperty("customerReference").GetString());
        var unit = Assert.Single(body.GetProperty("eligibleUnits").EnumerateArray());
        Assert.Equal(9200, unit.GetProperty("unitId").GetInt32());
        Assert.Equal("1204", unit.GetProperty("unitNumber").GetString());
        Assert.Equal("Tiger Tower", unit.GetProperty("projectName").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("unit").ValueKind);
    }

    [Fact]
    public async Task SelectedUnit_ReturnsUnitAndProjectDetails_WithSeparateHandoverDates()
    {
        var client = await SignInAsync();

        var response = await client.PostAsJsonAsync(Route, new GenesysCustomerUnitDetailsRequest("crm:9001", Phone, 9200));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("UnitDetails", body.GetProperty("mode").GetString());
        var unit = body.GetProperty("unit");
        Assert.Equal("Tower A", unit.GetProperty("tower").GetString());
        Assert.Equal(12, unit.GetProperty("floor").GetInt32());
        Assert.Equal(2, unit.GetProperty("bedrooms").GetInt32());
        Assert.Equal("sqft", unit.GetProperty("area").GetProperty("unit").GetString());
        Assert.Equal("9100", unit.GetProperty("booking").GetProperty("reference").GetString());
        Assert.Equal("Sold", unit.GetProperty("booking").GetProperty("status").GetString());
        Assert.Equal("P-114", unit.GetProperty("parking")[0].GetProperty("number").GetString());
        Assert.Equal("2027-06-30", unit.GetProperty("expectedHandoverDate").GetString());
        Assert.Equal(JsonValueKind.Null, unit.GetProperty("actualHandoverDate").ValueKind);
        var project = body.GetProperty("project");
        Assert.Equal(79, project.GetProperty("projectId").GetInt32());
        Assert.Equal("2027-03-31", project.GetProperty("expectedHandoverDate").GetString());
        Assert.Equal(JsonValueKind.Null, project.GetProperty("actualHandoverDate").ValueKind);
        Assert.Equal("Unit", body.GetProperty("handoverDateSource").GetString());
        Assert.Equal("Available", body.GetProperty("detailsStatus").GetString());
    }

    [Fact]
    public async Task WithoutProof_ProjectCompletionIsReturned_ButTheSaleIsWithheld()
    {
        var client = await SignInAsync();

        var body = await JsonAsync(await client.PostAsJsonAsync(Route, new GenesysCustomerUnitDetailsRequest("crm:9001", Phone, 9200)));

        Assert.Equal(62.5m, body.GetProperty("project").GetProperty("completionPercentage").GetDecimal());
        Assert.Equal("2027-01-31", body.GetProperty("project").GetProperty("expectedCompletionDate").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("project").GetProperty("actualCompletionDate").ValueKind);
        Assert.Equal("VerificationRequired", body.GetProperty("financialDetailsStatus").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("sale").ValueKind);
        Assert.DoesNotContain("1850000", body.ToString());
    }

    /// <summary>
    /// A confirmed session whose method says Otp but which the OTP service never produced (no recorded proof) — what an agent-asserted
    /// or forged session looks like. The sale stays withheld. The positive case (a session from a real, verified OTP) is
    /// <c>GenesysSmsOtpEndpointTests</c>.
    /// </summary>
    [Fact]
    public async Task ASessionThatOnlyAssertsOtp_DoesNotReleaseTheSale()
    {
        var (client, employeeId) = await SignInWithIdAsync();
        var session = await _factory.SeedConfirmedVerificationSessionAsync(employeeId, "9200");

        var body = await JsonAsync(await client.PostAsJsonAsync(Route, new GenesysCustomerUnitDetailsRequest("crm:9001", Phone, 9200, session)));

        Assert.Equal("VerificationFailed", body.GetProperty("financialDetailsStatus").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("sale").ValueKind);
        Assert.DoesNotContain("1850000", body.ToString());
        // The pre-existing structure is untouched.
        foreach (var member in new[] { "mode", "customerReference", "eligibleUnits", "unit", "project", "handoverDateSource", "detailsStatus" })
        {
            Assert.True(body.TryGetProperty(member, out _), member);
        }
    }

    [Fact]
    public async Task ProofOfAnotherAgent_OrAnUnknownSession_WithholdsTheSale()
    {
        var (client, _) = await SignInWithIdAsync();
        var foreign = await _factory.SeedConfirmedVerificationSessionAsync(Guid.NewGuid(), "9200");

        foreach (var session in new[] { foreign, Guid.NewGuid() })
        {
            var body = await JsonAsync(await client.PostAsJsonAsync(Route, new GenesysCustomerUnitDetailsRequest("crm:9001", Phone, 9200, session)));

            Assert.Equal("VerificationFailed", body.GetProperty("financialDetailsStatus").GetString());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("sale").ValueKind);
        }
    }

    [Fact]
    public async Task UnitOfAnotherCustomer_Returns403_WithTheCode_AndNoData()
    {
        var client = await SignInAsync();

        var response = await client.PostAsJsonAsync(Route, new GenesysCustomerUnitDetailsRequest("crm:9001", Phone, 5555));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(GenesysController.ErrorCodes.UnitNotEligible, body.GetProperty("code").GetString());
        Assert.False(body.TryGetProperty("unit", out _));
    }

    [Fact]
    public async Task CustomerReferenceOfSomeoneElse_Returns403()
    {
        var client = await SignInAsync();

        var response = await client.PostAsJsonAsync(Route, new GenesysCustomerUnitDetailsRequest("crm:1234", Phone, 9200));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            GenesysController.ErrorCodes.CustomerNotVerified,
            (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(null, "tel:+971500000900", null)]
    [InlineData("crm:9001", null, null)]
    [InlineData("crm:9001", "tel:+971500000900", 0)]
    public async Task MissingOrMalformedFields_Return400(string? customer, string? phone, int? unitId)
    {
        var client = await SignInAsync();

        var response = await client.PostAsJsonAsync(Route, new GenesysCustomerUnitDetailsRequest(customer, phone, unitId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        var response = await _factory.CreateClient()
            .PostAsJsonAsync(Route, new GenesysCustomerUnitDetailsRequest("crm:9001", Phone, 9200));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RoleWithoutCustomerVerificationAccess_Returns403()
    {
        var client = await SignInAsync(Roles.ReportingUser);

        var response = await client.PostAsJsonAsync(Route, new GenesysCustomerUnitDetailsRequest("crm:9001", Phone, 9200));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}

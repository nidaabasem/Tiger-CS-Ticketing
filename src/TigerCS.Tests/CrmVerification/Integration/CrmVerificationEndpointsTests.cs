using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TigerCS.Application.Modules.CrmVerification.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.CrmVerification.Integration;

/// <summary>
/// MVP-API-Contracts.md §2.1-§2.4, against the real Api host (real routing,
/// real authorization, real EF Core-mapped schema — InMemory provider — and
/// the real, feature-flagged MockCrmGateway fixture data), reusing the
/// Identity module's TigerCsApiFactory/SeedEmployeeAsync test infrastructure.
/// </summary>
public class CrmVerificationEndpointsTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public CrmVerificationEndpointsTests(TigerCsApiFactory factory) => _factory = factory;

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
    public async Task GetUnit_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/crm/units/CRM-UNIT-1001");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetUnit_KnownFixture_Returns200()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/crm/units/CRM-UNIT-1001");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var unit = await response.Content.ReadFromJsonAsync<UnitVerificationResponseDto>();
        Assert.Equal("1204", unit!.UnitNumber);
        Assert.Equal(2, unit.ContactCount);
    }

    [Fact]
    public async Task GetUnit_UnknownId_Returns404()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/crm/units/CRM-DOES-NOT-EXIST");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetUnit_SimulatedCrmOutage_Returns502()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/crm/units/CRM-UNIT-OUTAGE");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task SearchUnits_MissingUnitNumber_ReturnsValidationProblem()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/crm/units/search");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SearchUnits_MatchingUnitNumber_ReturnsFixture()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/crm/units/search?unitNumber=0507");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var units = await response.Content.ReadFromJsonAsync<List<UnitVerificationResponseDto>>();
        Assert.Single(units!);
        Assert.Equal("CRM-UNIT-1002", units![0].CrmUnitId);
    }

    [Fact]
    public async Task GetContacts_KnownUnit_ReturnsContactsWithRepresentativeLink()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/crm/units/CRM-UNIT-1002/contacts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var contacts = await response.Content.ReadFromJsonAsync<List<ContactVerificationResponseDto>>();
        Assert.Equal(2, contacts!.Count);
        var owner = contacts.Single(c => c.CrmContactId == "CRM-CONTACT-2003");
        var representative = contacts.Single(c => c.CrmContactId == "CRM-CONTACT-2004");
        Assert.Equal(owner.ContactReferenceId, representative.AuthorizedRepresentativeOfContactReferenceId);
    }

    private async Task<(int UnitReferenceId, int ContactReferenceId)> LookUpUnitAndFirstContactAsync(HttpClient client)
    {
        var unitResponse = await client.GetAsync("/api/crm/units/CRM-UNIT-1001");
        unitResponse.EnsureSuccessStatusCode();
        var unit = await unitResponse.Content.ReadFromJsonAsync<UnitVerificationResponseDto>();

        var contactsResponse = await client.GetAsync("/api/crm/units/CRM-UNIT-1001/contacts");
        contactsResponse.EnsureSuccessStatusCode();
        var contacts = await contactsResponse.Content.ReadFromJsonAsync<List<ContactVerificationResponseDto>>();

        return (unit!.UnitReferenceId, contacts![0].ContactReferenceId);
    }

    [Fact]
    public async Task CreateVerificationSession_FullFlow_Returns201Confirmed()
    {
        var client = await CreateAuthenticatedClientAsync();
        var (unitReferenceId, contactReferenceId) = await LookUpUnitAndFirstContactAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/verification-sessions",
            new CreateVerificationSessionRequestDto(unitReferenceId, contactReferenceId, true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<VerificationSessionResponseDto>();
        Assert.Equal("Confirmed", session!.Status);
        Assert.True(session.ConfirmedVerbally);
        Assert.Equal("1204", session.SnapshotUnitNumber);
    }

    [Fact]
    public async Task CreateVerificationSession_ConfirmedVerballyFalse_ReturnsValidationProblem()
    {
        var client = await CreateAuthenticatedClientAsync();
        var (unitReferenceId, contactReferenceId) = await LookUpUnitAndFirstContactAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/verification-sessions",
            new CreateVerificationSessionRequestDto(unitReferenceId, contactReferenceId, false));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateVerificationSession_UnknownUnitOrContact_Returns404()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/verification-sessions",
            new CreateVerificationSessionRequestDto(999_999, 999_999, true));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateVerificationSession_RepeatedIdempotencyKey_ReturnsSameSession()
    {
        var client = await CreateAuthenticatedClientAsync();
        var (unitReferenceId, contactReferenceId) = await LookUpUnitAndFirstContactAsync(client);
        var request = new CreateVerificationSessionRequestDto(unitReferenceId, contactReferenceId, true);

        var idempotencyKey = Guid.NewGuid().ToString();
        client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);

        var first = await client.PostAsJsonAsync("/api/verification-sessions", request);
        var second = await client.PostAsJsonAsync("/api/verification-sessions", request);

        var firstBody = await first.Content.ReadFromJsonAsync<VerificationSessionResponseDto>();
        var secondBody = await second.Content.ReadFromJsonAsync<VerificationSessionResponseDto>();
        Assert.Equal(firstBody!.VerificationSessionId, secondBody!.VerificationSessionId);
    }

    [Fact]
    public async Task GetVerificationSession_Owner_Returns200()
    {
        var client = await CreateAuthenticatedClientAsync();
        var (unitReferenceId, contactReferenceId) = await LookUpUnitAndFirstContactAsync(client);
        var created = await client.PostAsJsonAsync(
            "/api/verification-sessions", new CreateVerificationSessionRequestDto(unitReferenceId, contactReferenceId, true));
        var session = await created.Content.ReadFromJsonAsync<VerificationSessionResponseDto>();

        var response = await client.GetAsync($"/api/verification-sessions/{session!.VerificationSessionId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetVerificationSession_DifferentAgent_Returns403_SingleAgentOwnershipEnforced()
    {
        var ownerClient = await CreateAuthenticatedClientAsync();
        var (unitReferenceId, contactReferenceId) = await LookUpUnitAndFirstContactAsync(ownerClient);
        var created = await ownerClient.PostAsJsonAsync(
            "/api/verification-sessions", new CreateVerificationSessionRequestDto(unitReferenceId, contactReferenceId, true));
        var session = await created.Content.ReadFromJsonAsync<VerificationSessionResponseDto>();

        var otherAgentClient = await CreateAuthenticatedClientAsync();

        var response = await otherAgentClient.GetAsync($"/api/verification-sessions/{session!.VerificationSessionId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetVerificationSession_UnknownId_Returns404()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/verification-sessions/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

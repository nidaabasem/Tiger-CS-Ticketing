using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.CustomerVerification.Integration;

/// <summary>
/// Proves the CustomerVerification policy (PolicyNames.CustomerVerification) against
/// all 9 approved MVP roles (Roles.All) for the three CRM Verification
/// actions — CS Agent and CS Supervisor allowed by the policy's own role
/// list, System Administrator allowed by the ADR-0024 central override, and
/// every other role denied, including Reporting User and the two roles this
/// review's security pass flagged as an open
/// Solution-Analysis.md-vs-MVP-API-Contracts.md conflict (Department Head,
/// CS Manager) — resolved by explicit confirmation before coding, not
/// guessed. See PolicyNames.CustomerVerification's own remarks.
///
/// <para>
/// The System Administrator rows are the confirmed management decision
/// recorded in ADR-0024 superseding this policy's previous exclusion of the
/// role; the six denied roles are unchanged by it, which is what makes the
/// override an override rather than an open door.
/// </para>
/// </summary>
public class CustomerVerificationAuthorizationTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public CustomerVerificationAuthorizationTests(TigerCsApiFactory factory) => _factory = factory;

    public static IEnumerable<object[]> AllRolesWithExpectedAccess() =>
        Roles.All.Select(role => new object[]
        {
            role,
            role is Roles.CsAgent or Roles.CsSupervisor or Roles.SystemAdministrator
        });

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string role)
    {
        var (username, password, _) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        return client;
    }

    [Theory]
    [MemberData(nameof(AllRolesWithExpectedAccess))]
    public async Task GetUnit_EveryRole_MatchesCustomerVerificationPolicy(string role, bool expectAuthorized)
    {
        var client = await CreateAuthenticatedClientAsync(role);

        var response = await client.GetAsync("/api/crm/units/CRM-UNIT-1001");

        Assert.Equal(expectAuthorized ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AllRolesWithExpectedAccess))]
    public async Task GetContacts_EveryRole_MatchesCustomerVerificationPolicy(string role, bool expectAuthorized)
    {
        var client = await CreateAuthenticatedClientAsync(role);

        var response = await client.GetAsync("/api/crm/units/CRM-UNIT-1001/contacts");

        Assert.Equal(expectAuthorized ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AllRolesWithExpectedAccess))]
    public async Task CreateVerificationSession_EveryRole_MatchesCustomerVerificationPolicy(string role, bool expectAuthorized)
    {
        var client = await CreateAuthenticatedClientAsync(role);

        // An unauthorized role's own unit/contact lookup would itself 403
        // (proven by the two theories above) — since [Authorize] runs before
        // the action body, an unauthorized caller here posts placeholder IDs
        // and never reaches "unit not found" logic. An authorized caller
        // looks the real fixture up first, as an agent actually would.
        var unitReferenceId = 1;
        var contactReferenceId = 1;
        if (expectAuthorized)
        {
            var unitResponse = await client.GetAsync("/api/crm/units/CRM-UNIT-1001");
            unitResponse.EnsureSuccessStatusCode();
            var unit = await unitResponse.Content.ReadFromJsonAsync<UnitVerificationResponseDto>();

            var contactsResponse = await client.GetAsync("/api/crm/units/CRM-UNIT-1001/contacts");
            contactsResponse.EnsureSuccessStatusCode();
            var contacts = await contactsResponse.Content.ReadFromJsonAsync<List<ContactVerificationResponseDto>>();

            unitReferenceId = unit!.UnitReferenceId;
            contactReferenceId = contacts![0].ContactReferenceId;
        }

        var response = await client.PostAsJsonAsync(
            "/api/verification-sessions",
            new CreateVerificationSessionRequestDto(unitReferenceId, contactReferenceId, true, "ManualAgentConfirmation"));

        Assert.Equal(expectAuthorized ? HttpStatusCode.Created : HttpStatusCode.Forbidden, response.StatusCode);
    }
}

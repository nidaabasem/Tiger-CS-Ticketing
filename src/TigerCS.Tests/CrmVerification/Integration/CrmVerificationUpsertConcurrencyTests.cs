using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.CrmVerification.Integration;

/// <summary>
/// Two genuinely concurrent lookups of the same never-before-cached
/// CrmUnitId/CrmContactId, against a database this test class does not
/// share with any other test class (a fresh TigerCsApiFactory per
/// IClassFixture-scoped test class, per xUnit's own isolation rules) — so
/// neither fixture unit can already be cached before these tests run,
/// unlike CrmVerificationEndpointsTests' shared class fixture where other
/// tests already populate the cache first.
///
/// <para>
/// An honest, disclosed limitation, same shape as
/// CrmVerificationEndpointsTests.CreateVerificationSession_ConcurrentDuplicateRequests_NeitherCallerCrashes:
/// this class runs against the EF Core InMemory provider, which — verified
/// empirically here, not assumed — does not enforce a unique index at all
/// under genuine concurrent writes from separate DbContext instances (an
/// earlier version of these tests asserted "exactly one row" and failed,
/// both concurrent inserts succeeding and producing two rows). This is a
/// test-infrastructure limitation, not a production defect: the real app
/// (Program.cs) always runs against SQL Server, where
/// UnitReferences.CrmUnitId/ContactReferences.CrmContactId's unique indexes
/// (UnitReferenceConfiguration/ContactReferenceConfiguration) are real and
/// do throw on a race, which CrmVerificationUnitOfWork translates and
/// CrmVerificationAppService's UpsertUnitAsync/UpsertContactAsync recover
/// from — proven deterministically by
/// CrmVerificationAppServiceTests.GetUnitAsync_ConcurrentDuplicateWriteRace_ReturnsWinningRowInsteadOfThrowing
/// and its Contacts counterpart, which simulate the exact exception SQL
/// Server would produce. What these two tests *can* honestly prove under
/// InMemory: concurrent lookups never crash the endpoint (no 500)
/// regardless of whether the DB layer happened to catch the race.
/// </para>
/// </summary>
public class CrmVerificationUpsertConcurrencyTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public CrmVerificationUpsertConcurrencyTests(TigerCsApiFactory factory) => _factory = factory;

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var (username, password, _) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        return client;
    }

    [Fact]
    public async Task GetUnit_ConcurrentFirstLookups_NeitherCallerCrashes()
    {
        var client = await CreateAuthenticatedClientAsync();

        var responses = await Task.WhenAll(
            client.GetAsync("/api/crm/units/CRM-UNIT-1001"),
            client.GetAsync("/api/crm/units/CRM-UNIT-1001"));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    [Fact]
    public async Task GetContacts_ConcurrentFirstLookups_NeitherCallerCrashes()
    {
        var client = await CreateAuthenticatedClientAsync();

        var responses = await Task.WhenAll(
            client.GetAsync("/api/crm/units/CRM-UNIT-1002/contacts"),
            client.GetAsync("/api/crm/units/CRM-UNIT-1002/contacts"));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }
}

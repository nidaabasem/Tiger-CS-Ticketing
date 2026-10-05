using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Integrations.Modules.PactIntegration;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Collections.Edsm;

/// <summary>
/// The verified-mapping cache through the real host: DI registration, the
/// <c>GET api/collections/customers/by-key/{key}/payment-summary</c> controller,
/// authentication and the singleton <see cref="PactAccountMappingCache"/>.
/// PACT is <c>MockPactGateway</c> behind a call counter, and EDSM is the
/// Fixture provider. The Customer Directory read is replaced, so no tickets
/// need seeding. This exercises TigerCS's wiring, not a real EDSM.
/// </summary>
public sealed class EdsmPaymentSummaryApiTests : IDisposable
{
    private const string PactKey = "ext:Pact:3001";
    private const string PactPhone = "+971500000002";   // MockPactGateway's fixture customer: companies 4 and 25

    private readonly CountingPact _pact = new();
    private readonly TigerCsApiFactory _factory;

    public EdsmPaymentSummaryApiTests()
    {
        _factory = new TigerCsApiFactory
        {
            ExtraConfiguration = new()
            {
                ["Collections:Enabled"] = "true",
                ["CollectionsSource:EdsmProvider"] = "Fixture",
                ["CollectionsSource:EdsmNumberCulture"] = "en-US",
            },
            ExtraServices = services =>
            {
                services.AddScoped<ICollectionsCustomerProfiles, Profiles>();
                services.AddSingleton(_pact);
                services.AddScoped<IPactCustomerLookupGateway>(sp => sp.GetRequiredService<CountingPact>());
            },
        };
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void TheCache_IsOneSingleton_InjectedIntoEveryRequestScopesService()
    {
        using var first = _factory.Services.CreateScope();
        using var second = _factory.Services.CreateScope();

        Assert.NotSame(first.ServiceProvider.GetRequiredService<CollectionsPaymentSummaryAppService>(),
            second.ServiceProvider.GetRequiredService<CollectionsPaymentSummaryAppService>());
        Assert.Same(first.ServiceProvider.GetRequiredService<PactAccountMappingCache>(),
            second.ServiceProvider.GetRequiredService<PactAccountMappingCache>());
    }

    [Fact]
    public async Task RepeatedRequests_ThroughTheApi_DiscoverPactContractsOnce()
    {
        var client = await ClientAsync(Roles.CsAgent);
        var url = $"/api/collections/customers/by-key/{Uri.EscapeDataString(PactKey)}/payment-summary";

        var loads = new List<CollectionsPaymentSummaryResponseDto>();
        for (var i = 0; i < 4; i++)
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            loads.Add((await response.Content.ReadFromJsonAsync<CollectionsPaymentSummaryResponseDto>())!);
        }

        Assert.Equal(1, _pact.Calls);
        Assert.Equal("PactLookup", loads[0].MappingSource);
        Assert.All(loads.Skip(1), l => Assert.Equal("Cached", l.MappingSource));
        Assert.All(loads, l => Assert.Equal(loads[0].MappingVerifiedAtUtc, l.MappingVerifiedAtUtc));
        Assert.Equal([4, 25], loads[^1].Companies.Select(c => c.CompanyId));
        Assert.Equal(["Owned", "Rented"], loads[^1].Companies.Select(c => c.BusinessModel));
    }

    [Fact]
    public async Task ACachedMapping_DoesNotLetAnUnauthorizedRoleRead()
    {
        var agent = await ClientAsync(Roles.CsAgent);
        var url = $"/api/collections/customers/by-key/{Uri.EscapeDataString(PactKey)}/payment-summary";
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync(url)).StatusCode);

        var reporting = await ClientAsync(Roles.ReportingUser);
        Assert.Equal(HttpStatusCode.Forbidden, (await reporting.GetAsync(url)).StatusCode);
        Assert.Equal(1, _pact.Calls);
    }

    [Fact]
    public async Task ACrmCustomer_IsNotMapped_AndPactIsNotCalled()
    {
        var client = await ClientAsync(Roles.CsAgent);

        var dto = await client.GetFromJsonAsync<CollectionsPaymentSummaryResponseDto>(
            $"/api/collections/customers/by-key/{Uri.EscapeDataString("crm:9001")}/payment-summary");

        Assert.Equal("NotMapped", dto!.MappingStatus);
        Assert.Empty(dto.Companies);
        Assert.Null(dto.MappingSource);
        Assert.Equal(0, _pact.Calls);
    }

    private async Task<HttpClient> ClientAsync(string role)
    {
        var (username, password, _) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password))).Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    private sealed class CountingPact : IPactCustomerLookupGateway
    {
        private readonly MockPactGateway _inner = new();
        private int _calls;

        public int Calls => _calls;

        public Task<PactCustomerLookupResult> SearchByMobileAsync(string mobileNumber, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return _inner.SearchByMobileAsync(mobileNumber, cancellationToken);
        }
    }

    private sealed class Profiles : ICollectionsCustomerProfiles
    {
        public Task<CustomerDirectoryProfileResult> GetProfileAsync(
            Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, string customerKey, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            CustomerDirectoryProfileDto? profile = customerKey switch
            {
                PactKey => new(PactKey, "External", "Fatima Noor", [PactPhone], [], "Pact", null, "Pact", "3001", 0, 1, now, now, 1, [], [], []),
                "crm:9001" => new("crm:9001", "Crm", "Test Buyer", ["+971500000900"], [], "Crm", 9001, null, null, 0, 1, now, now, 1, [], [], []),
                _ => null
            };
            return Task.FromResult(profile is null
                ? CustomerDirectoryProfileResult.Failure(CustomerDirectoryProfileOutcome.NotFound)
                : CustomerDirectoryProfileResult.Success(profile));
        }
    }
}

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

    // ---- Genesys boundary: /api/genesys/collections (same service as the Payment tab) ----

    private static string G(string key, string tail) => $"/api/genesys/collections/customers/by-key/{Uri.EscapeDataString(key)}/{tail}";

    [Fact]
    public async Task Genesys_PaymentSummary_IsTheSameEdsmServiceAsThePaymentTab()
    {
        var client = await ClientAsync(Roles.CsAgent);

        var genesys = await client.GetFromJsonAsync<CollectionsPaymentSummaryResponseDto>(G(PactKey, "payment-summary?includeTransactions=false"));
        var web = await client.GetFromJsonAsync<CollectionsPaymentSummaryResponseDto>(
            $"/api/collections/customers/by-key/{Uri.EscapeDataString(PactKey)}/payment-summary");

        Assert.Equal("Fixture", genesys!.Source);                       // the EDSM gateway, not the Unavailable per-account provider
        Assert.Equal("Mapped", genesys.MappingStatus);
        Assert.Equal([4, 25], genesys.Companies.Select(c => c.CompanyId));
        Assert.All(genesys.Companies, c => Assert.Empty(c.Transactions));   // includeTransactions=false
        Assert.All(web!.Companies, c => Assert.Equal(3, c.Transactions.Count));
        Assert.Equal(genesys.Companies.SelectMany(c => c.Fields.Select(f => f.Raw)), web.Companies.SelectMany(c => c.Fields.Select(f => f.Raw)));
        Assert.Equal(1, _pact.Calls);                                   // one mapping, shared by both prefixes
    }

    [Theory]
    [InlineData("Paid", 1)]
    [InlineData("2", 2)]
    [InlineData("outstanding", 3)]
    public async Task Genesys_PaymentTransactions_ReadOnlyTypes(string type, int expectedId)
    {
        var client = await ClientAsync(Roles.CsAgent);

        var response = await client.GetAsync(G(PactKey, $"payment-transactions?companyId=4&type={type}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<CollectionsPaymentTransactionsResponseDto>())!;
        Assert.Equal(expectedId, dto.TransactionTypeId);
        Assert.Equal("Owned", dto.BusinessModel);
        Assert.Equal("AED", dto.Currency);
        Assert.Null(dto.SourceAsOfUtc);
        Assert.NotEmpty(dto.Items);
    }

    [Theory]
    [InlineData("All")]
    [InlineData("4")]
    [InlineData("")]
    [InlineData("5")]
    public async Task Genesys_PaymentTransactions_TypeAllAndUnknownTypes_Are400_WithoutAnyLookup(string type)
    {
        var client = await ClientAsync(Roles.CsAgent);

        var response = await client.GetAsync(G(PactKey, $"payment-transactions?companyId=25&type={type}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("InvalidRequest", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, _pact.Calls);
    }

    [Fact]
    public async Task Genesys_PaymentTransactions_ForACompanyOutsideThePactContracts_Is404()
    {
        var client = await ClientAsync(Roles.CsAgent);

        var response = await client.GetAsync(G(PactKey, "payment-transactions?companyId=32&type=Paid"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("not among this customer's confirmed PACT contracts", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Genesys_UnmappedCrmCustomer_GetsNoFinancialData()
    {
        var client = await ClientAsync(Roles.CsAgent);

        var summary = await client.GetFromJsonAsync<CollectionsPaymentSummaryResponseDto>(G("crm:9001", "payment-summary"));
        var history = await client.GetAsync(G("crm:9001", "payment-transactions?companyId=4&type=Paid"));

        Assert.Equal("NotMapped", summary!.MappingStatus);
        Assert.Empty(summary.Companies);
        Assert.Equal((HttpStatusCode)422, history.StatusCode);
        Assert.Contains("CustomerNotMapped", await history.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, _pact.Calls);
    }

    [Fact]
    public async Task Genesys_WithoutAToken_Is401_AndWithoutFinancialRead_Is403()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(G(PactKey, "payment-summary"))).StatusCode);

        var reporting = await ClientAsync(Roles.ReportingUser);
        Assert.Equal(HttpStatusCode.Forbidden, (await reporting.GetAsync(G(PactKey, "payment-summary"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reporting.GetAsync(G(PactKey, "payment-transactions?companyId=4&type=Paid"))).StatusCode);
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

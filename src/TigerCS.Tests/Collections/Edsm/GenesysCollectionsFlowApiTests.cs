using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TigerCS.Api.Controllers;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.GenesysIntegration;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Integrations.Modules.CollectionsIntegration;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Collections.Edsm;

/// <summary>
/// The Genesys inbound flow for a PACT customer TigerCS has never verified,
/// through the real host, real Customer Directory and real ticket creation.
/// PACT is the host's mock gateway and EDSM the Fixture provider, so this
/// proves the authorization path, not real EDSM data.
///
/// <para>
/// The rule it pins down: a phone match from the Genesys lookup is not an
/// identity. Payment data becomes readable only after an agent selects the
/// PACT customer while creating a ticket (the New Ticket wizard). A ticket
/// Genesys creates by itself stays unverified and unlocks nothing.
/// </para>
/// </summary>
public sealed class GenesysCollectionsFlowApiTests : IDisposable
{
    private const string PactKnownNumber = "+971500000002";   // the host's mock PACT customer (tenant 3001)

    private readonly TigerCsApiFactory _factory = new()
    {
        ExtraConfiguration = new()
        {
            ["Collections:Enabled"] = "true",
            ["CollectionsSource:EdsmProvider"] = "Fixture",
            ["CollectionsSource:EdsmNumberCulture"] = "en-US",
        },
    };

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task UnverifiedCustomer_HasNoPaymentData_UntilAnAgentVerifiesThemThroughPact()
    {
        var serviceAccount = await ClientAsync(Roles.CsAgent);    // what TigerGroupWeb signs in as
        _factory.CrmBuyerLookupGateway.Returns(CrmBuyerLookupResult.NotFound());   // a PACT-only caller
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Customer Service " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        await _factory.SeedDepartmentCustomerLookupSourceAsync(departmentId, CustomerLookupSource.Pact);

        // 1. Genesys looks the caller up: PACT matches the number.
        var lookup = await (await serviceAccount.GetAsync($"/api/genesys/customers/lookup?phoneNumber={Uri.EscapeDataString("tel:" + PactKnownNumber)}"))
            .Content.ReadFromJsonAsync<GenesysCustomerLookupResultDto>();
        Assert.Equal("Pact", lookup!.ScreenPop.VerificationSource);
        var customerKey = $"ext:Pact:{lookup.ScreenPop.ExternalCustomerId}";
        var summaryUrl = $"/api/genesys/collections/customers/by-key/{Uri.EscapeDataString(customerKey)}/payment-summary?includeTransactions=false";

        // 2. A phone match alone unlocks nothing.
        await AssertAccountNotFoundAsync(serviceAccount, summaryUrl);

        // 3. Nor does a ticket Genesys creates itself: it is an unverified inquiry.
        var queueId = await SeedQueueAsync(departmentId);
        var created = await serviceAccount.PostAsJsonAsync("/api/genesys/tickets",
            new GenesysInquiryRequest(Guid.NewGuid().ToString(), "Phone", CustomerPhone: "tel:" + PactKnownNumber, QueueId: queueId));
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        await AssertAccountNotFoundAsync(serviceAccount, summaryUrl);

        // 4. The approved path: an agent runs the department-scoped lookup and deliberately
        //    selects the PACT customer while creating the ticket.
        var agent = await ClientAsync(Roles.CsAgent);
        var categoryId = await _factory.CreateCategoryAsync("Payment inquiry", departmentId);
        var intake = await (await agent.PostAsJsonAsync("/api/intake-records",
            new CreateIntakeRecordRequestDto("Phone", PactKnownNumber, departmentId, false, null, null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();
        var agentLookup = await (await agent.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup"))
            .Content.ReadFromJsonAsync<CustomerLookupResultDto>();
        var pactCustomer = Assert.Single(Assert.Single(agentLookup!.Sources, s => s.Source == "Pact").Customers);
        var unit = pactCustomer.Units.First();
        var ticket = await agent.PostAsJsonAsync("/api/tickets", new CreateTicketRequestDto(
            intake.IntakeRecordId, UnitReferenceId: null, ContactReferenceId: null, categoryId, PriorityId: 3,
            RequestSummary: "Caller asked about their payments",
            ManualProjectName: unit.PropertyName, ManualUnitNumber: unit.UnitNumber,
            CustomerVerificationSource: "Pact", ExternalCustomerId: pactCustomer.ExternalCustomerId, ExternalUnitId: unit.ExternalUnitId));
        Assert.Equal(HttpStatusCode.Created, ticket.StatusCode);

        // 5. Now the light Genesys summary answers, complete, without transaction lists.
        var summaryResponse = await serviceAccount.GetAsync(summaryUrl);
        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        var summary = await summaryResponse.Content.ReadFromJsonAsync<CollectionsPaymentSummaryResponseDto>();
        Assert.Equal("Mapped", summary!.MappingStatus);
        Assert.Equal(CollectionsCompleteness.Complete, summary.Completeness);
        Assert.Equal("Fixture", summary.Source);
        Assert.All(summary.Companies, c => Assert.Empty(c.Transactions));

        // 6. Amounts and dates come from the separate transactions read, per selected company and type.
        var companyId = summary.Companies.First(c => c.Status == "Available").CompanyId;
        var transactions = await serviceAccount.GetAsync(
            $"/api/genesys/collections/customers/by-key/{Uri.EscapeDataString(customerKey)}/payment-transactions?companyId={companyId}&type=Paid");
        Assert.Equal(HttpStatusCode.OK, transactions.StatusCode);
        var rows = await transactions.Content.ReadFromJsonAsync<CollectionsPaymentTransactionsResponseDto>();
        Assert.Equal(companyId, rows!.CompanyId);
        Assert.Equal("Paid", rows.TransactionType);
    }

    [Fact]
    public async Task ARoleWithoutFinancialRead_IsRefused_EvenForAVerifiedCustomer()
    {
        var reporting = await ClientAsync(Roles.ReportingUser);

        var response = await reporting.GetAsync(
            $"/api/genesys/collections/customers/by-key/{Uri.EscapeDataString("ext:Pact:3001")}/payment-summary?includeTransactions=false");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task AssertAccountNotFoundAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("AccountNotFound", await response.Content.ReadAsStringAsync());
    }

    private async Task<string> SeedQueueAsync(int departmentId)
    {
        var queueId = "queue-" + Guid.NewGuid().ToString("N");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        db.GenesysQueueMappings.Add(new GenesysQueueMapping(queueId, "Customer Service", departmentId, DateTime.UtcNow));
        await db.SaveChangesAsync();
        return queueId;
    }

    private async Task<HttpClient> ClientAsync(string role)
    {
        var (username, password, _) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password))).Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }
}

/// <summary>
/// The deadline and cancellation through the real host: the Genesys prefix's
/// default, the caller's shorter header, and a client disconnect reaching the
/// EDSM call. The Customer Directory read is replaced and company 25's EDSM
/// summary never answers on its own.
/// </summary>
public sealed class CollectionsDeadlineApiTests : IDisposable
{
    private const string PactKey = "ext:Pact:3001";
    private const string PactPhone = "+971500000002";   // mock PACT: companies 4 and 25

    private readonly HangingEdsm _edsm = new();
    private readonly TigerCsApiFactory _factory;

    public CollectionsDeadlineApiTests()
    {
        _factory = new TigerCsApiFactory
        {
            ExtraConfiguration = new()
            {
                ["Collections:Enabled"] = "true",
                ["CollectionsSource:EdsmProvider"] = "Fixture",
                ["CollectionsSource:EdsmNumberCulture"] = "en-US",
                ["CollectionsSource:GenesysReadDeadlineSeconds"] = "2",
            },
            ExtraServices = services =>
            {
                services.AddScoped<ICollectionsCustomerProfiles, Profiles>();
                services.AddSingleton(_edsm);
                services.AddScoped<IEdsmCollectionsGateway>(sp =>
                {
                    var hanging = sp.GetRequiredService<HangingEdsm>();
                    hanging.Inner ??= new FixtureEdsmCollectionsGateway(sp.GetRequiredService<IOptions<CollectionsEdsmOptions>>());
                    return hanging;
                });
            },
        };
    }

    public void Dispose() => _factory.Dispose();

    private static string Url(string prefix) =>
        $"/api/{prefix}/customers/by-key/{Uri.EscapeDataString(PactKey)}/payment-summary?includeTransactions=false";

    [Fact]
    public async Task GenesysPrefix_UsesItsConfiguredDeadline_AndAnswersPartial()
    {
        var client = await ClientAsync();

        var started = DateTime.UtcNow;
        var response = await client.GetAsync(Url("genesys/collections"));
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.InRange(elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(10));
        var dto = await response.Content.ReadFromJsonAsync<CollectionsPaymentSummaryResponseDto>();
        Assert.Equal(CollectionsCompleteness.Partial, dto!.Completeness);
        Assert.Equal("Available", dto.Companies.Single(c => c.CompanyId == 4).Status);
        Assert.Equal(CollectionsCompleteness.DeadlineExceeded, dto.Companies.Single(c => c.CompanyId == 25).Status);
        Assert.Contains("2 s deadline", dto.Companies.Single(c => c.CompanyId == 25).StatusDetail);
    }

    [Fact]
    public async Task TheCallersHeader_ShortensTheDeadline()
    {
        var client = await ClientAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("genesys/collections"));
        request.Headers.Add(CollectionsEdsmOptions.DeadlineHeader, "1");

        var response = await client.SendAsync(request);

        var dto = await response.Content.ReadFromJsonAsync<CollectionsPaymentSummaryResponseDto>();
        Assert.Contains("1 s deadline", dto!.Companies.Single(c => c.CompanyId == 25).StatusDetail);
    }

    [Fact]
    public async Task TheCallersHeader_CannotLengthenTheDeadline()
    {
        var client = await ClientAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("genesys/collections"));
        request.Headers.Add(CollectionsEdsmOptions.DeadlineHeader, "120");

        var dto = await (await client.SendAsync(request)).Content.ReadFromJsonAsync<CollectionsPaymentSummaryResponseDto>();

        Assert.Contains("2 s deadline", dto!.Companies.Single(c => c.CompanyId == 25).StatusDetail);
    }

    [Fact]
    public async Task AClientDisconnect_CancelsTheEdsmCall()
    {
        var client = await ClientAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("collections"));   // Web prefix: 60 s default, so only the disconnect can stop it
        using var disconnect = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(request, disconnect.Token));

        var waited = TimeSpan.Zero;
        while (!_edsm.SawCancellation && waited < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(50);
            waited += TimeSpan.FromMilliseconds(50);
        }

        Assert.True(_edsm.SawCancellation, "EDSM's call never saw the client's disconnect.");
    }

    private async Task<HttpClient> ClientAsync()
    {
        var (username, password, _) = await _factory.SeedEmployeeAsync(Roles.CsAgent);
        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password))).Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    /// <summary>Fixture EDSM, except company 25's summary, which waits until cancelled.</summary>
    private sealed class HangingEdsm : IEdsmCollectionsGateway
    {
        public FixtureEdsmCollectionsGateway? Inner { get; set; }

        public volatile bool SawCancellation;

        public string SourceName => "Fixture";

        public async Task<EdsmResult<EdsmPaymentSummary>> GetPaymentSummaryAsync(int companyId, string tenantId, CancellationToken cancellationToken = default)
        {
            if (companyId != 25)
            {
                return await Inner!.GetPaymentSummaryAsync(companyId, tenantId, cancellationToken);
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                throw;
            }

            throw new InvalidOperationException("unreachable");
        }

        public Task<EdsmResult<EdsmPaymentTransactions>> GetPaymentTransactionsAsync(
            int companyId, string tenantId, string mobile, EdsmTransactionType type, CancellationToken cancellationToken = default) =>
            Inner!.GetPaymentTransactionsAsync(companyId, tenantId, mobile, type, cancellationToken);

        public Task<EdsmResult<IReadOnlyList<EdsmDueInstallment>>> GetDueInstallmentsAsync(
            int companyId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default) =>
            Inner!.GetDueInstallmentsAsync(companyId, fromDate, toDate, cancellationToken);
    }

    private sealed class Profiles : ICollectionsCustomerProfiles
    {
        public Task<CustomerDirectoryProfileResult> GetProfileAsync(
            Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, string customerKey, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            return Task.FromResult(customerKey == PactKey
                ? CustomerDirectoryProfileResult.Success(new(PactKey, "External", "Fatima Noor", [PactPhone], [], "Pact", null, "Pact", "3001", 0, 1, now, now, 1, [], [], []))
                : CustomerDirectoryProfileResult.Failure(CustomerDirectoryProfileOutcome.NotFound));
        }
    }
}

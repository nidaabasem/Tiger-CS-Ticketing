using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Integrations.Modules.CollectionsIntegration;
using TigerCS.Integrations.Modules.PactIntegration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;

namespace TigerCS.Tests.Collections.Edsm;

/// <summary>
/// EDSM's payment summary (PACT <c>v1/reports/payment-summary</c>): strict
/// amount parsing (nothing unparseable becomes zero), the unconfirmed
/// envelope, the HTTP contract on the existing PactApi client, and the
/// tenant → company mapping through PACT's own contracts rows.
/// </summary>
public sealed class EdsmAmountParserTests
{
    [Theory]
    [InlineData("1250000.00", 1250000.00)]
    [InlineData("0", 0)]
    [InlineData("0.00", 0)]
    [InlineData(" 42.5 ", 42.5)]
    [InlineData("-300.25", -300.25)]
    public void PlainInvariantValues_AreProvided(string raw, double expected)
    {
        var amount = EdsmAmountParser.Parse(raw, EdsmAmountFormat.PlainInvariant);

        Assert.Equal(EdsmAmountStatus.Provided, amount.Status);
        Assert.Equal((decimal)expected, amount.Value);
        Assert.Equal(raw, amount.Raw);
    }

    [Theory]
    [InlineData("1,250.00")]     // grouping not confirmed
    [InlineData("1.250,00")]     // decimal comma
    [InlineData("1,25")]         // ambiguous
    [InlineData("AED 100.00")]
    [InlineData("100.00 AED")]
    [InlineData("1e3")]
    [InlineData("$100")]
    [InlineData("12.")]
    [InlineData(".5")]
    [InlineData("N/A")]
    [InlineData("--5")]
    [InlineData("(100.00)")]
    public void AnythingElse_IsUnreadable_NeverZero(string raw)
    {
        var amount = EdsmAmountParser.Parse(raw, EdsmAmountFormat.PlainInvariant);

        Assert.Equal(EdsmAmountStatus.Unreadable, amount.Status);
        Assert.Null(amount.Value);
        Assert.Equal(raw, amount.Raw);
    }

    [Fact]
    public void NullIsMissing_AndBlankIsEmpty_BothWithoutAValue()
    {
        Assert.Equal(EdsmAmount.Missing, EdsmAmountParser.Parse(null, EdsmAmountFormat.PlainInvariant));

        var blank = EdsmAmountParser.Parse("  ", EdsmAmountFormat.PlainInvariant);
        Assert.Equal(EdsmAmountStatus.Empty, blank.Status);
        Assert.Null(blank.Value);
    }

    [Theory]
    [InlineData("1,250.00", 1250.00)]
    [InlineData("1,250,000.50", 1250000.50)]
    [InlineData("950.00", 950.00)]
    public void GroupedFormat_AcceptsThreeDigitGroups_OnlyWhenConfigured(string raw, double expected)
    {
        var amount = EdsmAmountParser.Parse(raw, EdsmAmountFormat.GroupedInvariant);

        Assert.Equal(EdsmAmountStatus.Provided, amount.Status);
        Assert.Equal((decimal)expected, amount.Value);
    }

    [Theory]
    [InlineData("1,25.00")]
    [InlineData("12,50")]
    [InlineData("1.250,00")]
    [InlineData(",250")]
    public void GroupedFormat_StillRejectsMalformedGroups(string raw) =>
        Assert.Equal(EdsmAmountStatus.Unreadable, EdsmAmountParser.Parse(raw, EdsmAmountFormat.GroupedInvariant).Status);

    [Fact]
    public void HighPrecision_IsKeptExactly()
    {
        var amount = EdsmAmountParser.Parse("1234.5678", EdsmAmountFormat.PlainInvariant);

        Assert.Equal(1234.5678m, amount.Value);
    }
}

public sealed class EdsmPaymentSummaryParseTests
{
    private static EdsmPaymentSummaryResult Parse(string body) => EdsmPaymentSummaryHttpGateway.Parse(body, EdsmAmountFormat.PlainInvariant);

    [Fact]
    public void ADataWrappedCamelCaseBody_IsRead()
    {
        var result = Parse("""{"data":{"totalAmount":"1000.00","paidAmount":"400.00","dueAmount":"100.00","outstandingAmount":"600.00","lateFines":"25.00"}}""");

        Assert.Equal(EdsmPaymentSummaryOutcome.Success, result.Outcome);
        Assert.Equal("data", result.Envelope);
        var s = result.Summary!;
        Assert.Equal(1000.00m, s.TotalAmount.Value);
        Assert.Equal(400.00m, s.PaidAmount.Value);
        Assert.Equal(100.00m, s.DueAmount.Value);
        Assert.Equal(600.00m, s.OutstandingAmount.Value);
        Assert.Equal(25.00m, s.LateFines.Value);
    }

    [Fact]
    public void ABarePascalCaseBody_IsRead()
    {
        var result = Parse("""{"TotalAmount":"1000.00","PaidAmount":"400.00","DueAmount":"100.00","OutstandingAmount":"600.00","LateFines":"0.00"}""");

        Assert.Equal(EdsmPaymentSummaryOutcome.Success, result.Outcome);
        Assert.Equal("bare", result.Envelope);
        Assert.Equal(0.00m, result.Summary!.LateFines.Value);
        Assert.Equal(EdsmAmountStatus.Provided, result.Summary.LateFines.Status);   // an explicit "0.00" is a real zero
    }

    [Fact]
    public void MissingNullEmptyAndNonStringFields_KeepTheirStatus_AndNoValue()
    {
        var result = Parse("""{"data":{"totalAmount":"","paidAmount":null,"dueAmount":150,"outstandingAmount":"1,000.00"}}""");

        var s = result.Summary!;
        Assert.Equal(EdsmAmountStatus.Empty, s.TotalAmount.Status);
        Assert.Equal(EdsmAmountStatus.Missing, s.PaidAmount.Status);
        Assert.Equal(EdsmAmountStatus.Unreadable, s.DueAmount.Status);
        Assert.Equal("150", s.DueAmount.Raw);
        Assert.Equal(EdsmAmountStatus.Unreadable, s.OutstandingAmount.Status);
        Assert.Equal(EdsmAmountStatus.Missing, s.LateFines.Status);
        Assert.All([s.TotalAmount, s.PaidAmount, s.DueAmount, s.OutstandingAmount, s.LateFines], a => Assert.Null(a.Value));
    }

    [Theory]
    [InlineData("""{"data":[{"totalAmount":"1.00"}]}""")]
    [InlineData("""{"data":null}""")]
    [InlineData("""{"data":"1000"}""")]
    [InlineData("""{"message":"ok","success":true}""")]
    [InlineData("""[{"totalAmount":"1.00"}]""")]
    [InlineData("\"1000\"")]
    [InlineData("<html>error</html>")]
    [InlineData("")]
    public void UnrecognisedBodies_AreInvalidResponses(string body)
    {
        var result = Parse(body);

        Assert.Equal(EdsmPaymentSummaryOutcome.InvalidResponse, result.Outcome);
        Assert.Null(result.Summary);
    }

    [Fact]
    public void ANullPayloadWithAnEnvelopeMessage_IsInvalid_AndCarriesTheMessage()
    {
        var result = Parse("""{"data":null,"message":"Company not supported"}""");

        Assert.Equal(EdsmPaymentSummaryOutcome.InvalidResponse, result.Outcome);
        Assert.Contains("Company not supported", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LateFines_AreNeverAddedToAnotherField()
    {
        var s = Parse("""{"data":{"dueAmount":"100.00","lateFines":"25.00"}}""").Summary!;

        Assert.Equal(100.00m, s.DueAmount.Value);
        Assert.Equal(25.00m, s.LateFines.Value);
    }
}

public sealed class EdsmPaymentSummaryHttpGatewayTests
{
    private const string BaseUrl = "https://pact.example.test/api/";
    private const string ApiKey = "pact-test-key";

    private static EdsmPaymentSummaryHttpGateway Gateway(StubHttpMessageHandler handler, string? apiKey = ApiKey, string? baseUrl = BaseUrl) =>
        new(new HttpClient(handler) { BaseAddress = baseUrl is null ? null : new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(5) },
            Options.Create(new PactApiOptions { BaseUrl = baseUrl, ApiKey = apiKey }),
            Options.Create(new CollectionsSourceOptions()),
            NullLogger<EdsmPaymentSummaryHttpGateway>.Instance);

    private static StubHttpMessageHandler Answer(HttpStatusCode status, string body = "") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") }));

    [Fact]
    public async Task TheRequest_IsTheDocumentedRoute_WithTheApiKeyHeader()
    {
        var handler = Answer(HttpStatusCode.OK, """{"data":{"totalAmount":"1.00"}}""");

        var result = await Gateway(handler).GetPaymentSummaryAsync(12, "3001");

        Assert.Equal(EdsmPaymentSummaryOutcome.Success, result.Outcome);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("https://pact.example.test/api/v1/reports/payment-summary?CompanyId=12&TenantId=3001", handler.LastRequest.RequestUri!.AbsoluteUri);
        Assert.Equal(ApiKey, Assert.Single(handler.LastRequest.Headers.GetValues("X-API-KEY")));
    }

    [Fact]
    public async Task AgainstTheConfiguredRootBaseUrl_TheUrlMatchesPactServicesOwnComposition()
    {
        // PactService.PaymentSummaryAsync sends "/v1/reports/payment-summary?..." (leading slash) on the same
        // HttpClient base; against the configured root BaseUrl both resolve to the same absolute URL.
        const string configured = "http://10.30.10.117:6020/";
        var handler = Answer(HttpStatusCode.OK, """{"data":{"totalAmount":"1.00"}}""");

        await Gateway(handler, baseUrl: configured).GetPaymentSummaryAsync(1, "3001");

        var pactService = new Uri(new Uri(configured), "/v1/reports/payment-summary?CompanyId=1&TenantId=3001");
        Assert.Equal(pactService.AbsoluteUri, handler.LastRequest!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task ABaseUrlWithAPathPrefix_KeepsThePrefix()
    {
        // The one place the two compositions differ: a leading slash would drop "/api". Ours keeps it,
        // like PactCustomerHttpGateway; the deployed BaseUrl has no prefix, so they agree there.
        var handler = Answer(HttpStatusCode.OK, """{"data":{"totalAmount":"1.00"}}""");

        await Gateway(handler, baseUrl: "https://pact.example.test/api/").GetPaymentSummaryAsync(1, "3001");

        Assert.StartsWith("https://pact.example.test/api/v1/reports/payment-summary", handler.LastRequest!.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTenantId_IsAStringAsInPactService_AndIsEscaped()
    {
        var handler = Answer(HttpStatusCode.OK, """{"data":{"totalAmount":"1.00"}}""");

        await Gateway(handler).GetPaymentSummaryAsync(1, "30&01");

        Assert.EndsWith("CompanyId=1&TenantId=30%2601", handler.LastRequest!.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, EdsmPaymentSummaryOutcome.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized, EdsmPaymentSummaryOutcome.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, EdsmPaymentSummaryOutcome.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest, EdsmPaymentSummaryOutcome.InvalidResponse)]
    [InlineData(HttpStatusCode.InternalServerError, EdsmPaymentSummaryOutcome.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, EdsmPaymentSummaryOutcome.Unavailable)]
    public async Task StatusCodes_MapToOutcomes_WithNoSummary(HttpStatusCode status, EdsmPaymentSummaryOutcome expected)
    {
        var result = await Gateway(Answer(status)).GetPaymentSummaryAsync(1, "3001");

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Summary);
    }

    [Fact]
    public async Task NoApiKey_IsUnavailable_WithoutCallingPact()
    {
        var handler = Answer(HttpStatusCode.OK, "{}");

        var result = await Gateway(handler, apiKey: " ").GetPaymentSummaryAsync(1, "3001");

        Assert.Equal(EdsmPaymentSummaryOutcome.Unavailable, result.Outcome);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task NoBaseUrl_IsUnavailable()
    {
        var result = await Gateway(Answer(HttpStatusCode.OK, "{}"), baseUrl: null).GetPaymentSummaryAsync(1, "3001");

        Assert.Equal(EdsmPaymentSummaryOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task ATimeoutOrNetworkFailure_IsUnavailable()
    {
        var timeout = new StubHttpMessageHandler((_, _) => throw new TaskCanceledException("timeout"));
        var network = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("refused"));

        Assert.Equal(EdsmPaymentSummaryOutcome.Unavailable, (await Gateway(timeout).GetPaymentSummaryAsync(1, "3001")).Outcome);
        Assert.Equal(EdsmPaymentSummaryOutcome.Unavailable, (await Gateway(network).GetPaymentSummaryAsync(1, "3001")).Outcome);
    }

    [Fact]
    public async Task AnHtmlErrorPageWith200_IsAnInvalidResponse()
    {
        var result = await Gateway(Answer(HttpStatusCode.OK, "<html>Login</html>")).GetPaymentSummaryAsync(1, "3001");

        Assert.Equal(EdsmPaymentSummaryOutcome.InvalidResponse, result.Outcome);
    }

    [Fact]
    public async Task PactContractsLookup_NowCarriesEachRowsCompanyId()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"data":[
                  {"tenantID":3001,"companyID":1,"unitID":41230,"unitNumber":"0304","contractID":88001,"projectName":"Tiger Marina Residences","customerBuyerType":2},
                  {"tenantID":3001,"companyID":2,"unitID":51200,"unitNumber":"1101","contractID":99002,"projectName":"Tiger Heights","customerBuyerType":2},
                  {"tenantID":3001,"unitID":61200,"unitNumber":"0201","contractID":99003,"customerBuyerType":2}
                ]}
                """, System.Text.Encoding.UTF8, "application/json")
        }));
        var gateway = new PactCustomerHttpGateway(new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) },
            Options.Create(new PactApiOptions { BaseUrl = BaseUrl, ApiKey = ApiKey }), NullLogger<PactCustomerHttpGateway>.Instance);

        var result = await gateway.SearchByMobileAsync("971500000002");

        var contracts = Assert.Single(result.Customers!).Contracts;
        Assert.Equal([1, 2, null], contracts.Select(c => c.CompanyId));
    }
}

public sealed class CollectionsPaymentSummaryAppServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);
    private const string PactKey = "ext:Pact:3001";
    private const string Phone = "+971500000002";

    private readonly CollectionsOptions _options = new() { Enabled = true };
    private readonly FakeProfiles _profiles = new();
    private readonly FakePactCustomerLookupGateway _pact = new();
    private readonly FakeEdsm _edsm = new();
    private readonly CollectionsCaller _agent = new(Guid.NewGuid(), [Roles.CsAgent], []);

    private CollectionsPaymentSummaryAppService Service() => new(
        _options,
        new CollectionsAuthorizationService(_options, new FakeDepartmentRepository()),
        new CollectionsClock(_options, new FakeTimeProvider(Now)),
        _profiles, _pact, _edsm);

    private static CustomerDirectoryProfileDto Profile(string key, int? crmId, string? source, string? externalId, params string[] phones) =>
        new(key, crmId is null ? (source is null ? "Phone" : "External") : "Crm", "Customer", phones, [], source ?? (crmId is null ? "Unverified" : "Crm"),
            crmId, source, externalId, 0, 1, Now.AddDays(-10), Now.AddDays(-1), 1, [], [], []);

    private static PactCustomerMatchDto Tenant(string tenantId, params PactContractDto[] contracts) =>
        new(tenantId, "Customer", Phone, null, "2", contracts);

    private static readonly EdsmPaymentSummary Sample = new(
        new(EdsmAmountStatus.Provided, 1000m, "1000.00"), EdsmAmount.Missing, new(EdsmAmountStatus.Unreadable, null, "1,000.00"),
        new(EdsmAmountStatus.Provided, 600m, "600.00"), new(EdsmAmountStatus.Empty, null, ""));

    [Fact]
    public async Task APactCustomer_IsResolvedToEachCompanyFromPactsOwnRows_AndAmountsPassThroughUnchanged()
    {
        _profiles.Add(Profile(PactKey, null, "Pact", "3001", Phone));
        _pact.Seed(Phone, Tenant("3001",
            new PactContractDto("41230", "88001", "0304", "Tiger Marina Residences", "Residential", 1),
            new PactContractDto("51200", "99002", "1101", "Tiger Heights", "Residential", 2)));
        _pact.Seed(Phone, Tenant("4444", new PactContractDto("70000", "77777", "0101", "Other", "Residential", 3)));
        _edsm.Answers[(1, "3001")] = EdsmPaymentSummaryResult.Success(Sample, "data");
        _edsm.Answers[(2, "3001")] = EdsmPaymentSummaryResult.Failure(EdsmPaymentSummaryOutcome.Unavailable, "timed out");

        var result = await Service().GetAsync(_agent, PactKey);

        Assert.Equal(CollectionsOutcome.Success, result.Outcome);
        var dto = result.Value!;
        Assert.Equal("Mapped", dto.MappingStatus);
        Assert.Equal("3001", dto.PactTenantId);
        Assert.Equal([(1, "3001"), (2, "3001")], _edsm.Calls);                 // never tenant 4444's company 3
        Assert.Equal(Now, dto.RetrievedAtUtc);
        Assert.Null(dto.SourceAsOfUtc);
        Assert.Null(dto.Currency);
        Assert.False(dto.FieldDefinitionsConfirmed);
        Assert.False(dto.InstalmentDetailAvailable);
        Assert.False(dto.TransactionDetailAvailable);

        var first = dto.Companies[0];
        Assert.Equal("Available", first.Status);
        Assert.Equal("data", first.Envelope);
        Assert.Equal("88001", Assert.Single(first.Contracts).ContractNumber);
        Assert.Equal(1000m, first.TotalAmount!.Value);
        Assert.Equal("Missing", first.PaidAmount!.Status);
        Assert.Null(first.PaidAmount.Value);
        Assert.Equal("Unreadable", first.DueAmount!.Status);
        Assert.Equal("1,000.00", first.DueAmount.Raw);
        Assert.Equal("Empty", first.LateFines!.Status);

        var second = dto.Companies[1];
        Assert.Equal("Unavailable", second.Status);
        Assert.Null(second.TotalAmount);
    }

    [Fact]
    public async Task ACrmCustomer_IsNotMapped_AndNeitherPactNorEdsmIsCalled()
    {
        _profiles.Add(Profile("crm:9001", 9001, null, null, Phone));

        var result = await Service().GetAsync(_agent, "crm:9001");

        Assert.Equal("NotMapped", result.Value!.MappingStatus);
        Assert.Contains("Tiger CRM (customerId 9001)", result.Value.MappingDetail, StringComparison.Ordinal);
        Assert.Equal(0, _pact.SearchCallCount);
        Assert.Empty(_edsm.Calls);
    }

    [Fact]
    public async Task ATenantPactNoLongerReturns_IsNotMapped()
    {
        _profiles.Add(Profile(PactKey, null, "Pact", "3001", Phone));
        _pact.Seed(Phone, Tenant("4444", new PactContractDto("70000", "77777", "0101", "Other", "Residential", 3)));

        var result = await Service().GetAsync(_agent, PactKey);

        Assert.Equal("NotMapped", result.Value!.MappingStatus);
        Assert.Empty(_edsm.Calls);
    }

    [Fact]
    public async Task ContractsWithoutACompany_AreReported_NotGuessed()
    {
        _profiles.Add(Profile(PactKey, null, "Pact", "3001", Phone));
        _pact.Seed(Phone, Tenant("3001", new PactContractDto("41230", "88001", "0304", "Tiger Marina Residences", "Residential")));

        var result = await Service().GetAsync(_agent, PactKey);

        Assert.Equal("NotMapped", result.Value!.MappingStatus);
        Assert.Single(result.Value.ContractsWithoutCompany);
        Assert.Empty(_edsm.Calls);
    }

    [Fact]
    public async Task PactUnreachable_IsFinanceUnavailable()
    {
        _profiles.Add(Profile(PactKey, null, "Pact", "3001", Phone));
        _pact.ForcedOutcome = PactCustomerLookupOutcome.Unavailable;

        var result = await Service().GetAsync(_agent, PactKey);

        Assert.Equal(CollectionsOutcome.FinanceUnavailable, result.Outcome);
        Assert.Empty(_edsm.Calls);
    }

    [Fact]
    public async Task Disabled_Forbidden_InvalidAndInvisibleCustomers_AreRefused()
    {
        _profiles.Add(Profile(PactKey, null, "Pact", "3001", Phone));

        Assert.Equal(CollectionsOutcome.Forbidden,
            (await Service().GetAsync(new CollectionsCaller(Guid.NewGuid(), [Roles.ReportingUser], []), PactKey)).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await Service().GetAsync(_agent, "not-a-key")).Outcome);
        Assert.Equal(CollectionsOutcome.AccountNotFound, (await Service().GetAsync(_agent, "ext:Pact:9999")).Outcome);

        _options.Enabled = false;
        Assert.Equal(CollectionsOutcome.Disabled, (await Service().GetAsync(_agent, PactKey)).Outcome);
    }

    private sealed class FakeProfiles : ICollectionsCustomerProfiles
    {
        private readonly Dictionary<string, CustomerDirectoryProfileDto> _profiles = [];

        public void Add(CustomerDirectoryProfileDto profile) => _profiles[profile.CustomerKey] = profile;

        public Task<CustomerDirectoryProfileResult> GetProfileAsync(
            Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, string customerKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(!customerKey.Contains(':', StringComparison.Ordinal)
                ? CustomerDirectoryProfileResult.Failure(CustomerDirectoryProfileOutcome.InvalidKey)
                : _profiles.TryGetValue(customerKey, out var profile)
                    ? CustomerDirectoryProfileResult.Success(profile)
                    : CustomerDirectoryProfileResult.Failure(CustomerDirectoryProfileOutcome.NotFound));
    }

    private sealed class FakeEdsm : IEdsmPaymentSummaryGateway
    {
        public Dictionary<(int, string), EdsmPaymentSummaryResult> Answers { get; } = [];
        public List<(int, string)> Calls { get; } = [];
        public string SourceName => "Test";

        public Task<EdsmPaymentSummaryResult> GetPaymentSummaryAsync(int companyId, string tenantId, CancellationToken cancellationToken = default)
        {
            Calls.Add((companyId, tenantId));
            return Task.FromResult(Answers.TryGetValue((companyId, tenantId), out var answer)
                ? answer
                : EdsmPaymentSummaryResult.Failure(EdsmPaymentSummaryOutcome.NotFound, "none"));
        }
    }
}

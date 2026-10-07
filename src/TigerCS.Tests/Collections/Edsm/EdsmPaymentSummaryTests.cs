using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Edsm;

/// <summary>
/// EDSM Collections routes per docs/Collections/EDSM_Collections_Contract.md:
/// <list type="bullet">
/// <item>the {title, status, data} envelope and the error matrix (§1);</item>
/// <item><c>#,##0.00</c> amounts read only in a configured culture (§7);</item>
/// <item>owned and rented definitions kept apart (§3.4, §3.5);</item>
/// <item>accounts confirmed through PACT contracts, never by EDSM's zeros (§1.2, §2.3);</item>
/// <item>read-only transaction types 1–3 only (§8.3);</item>
/// <item>due-installments filtered to the tenant, with company 20 unsupported (§4).</item>
/// </list>
/// These test code paths against fixture bodies in EDSM's wire shape. They do
/// not validate a deployed EDSM or UAT data.
/// </summary>
public sealed class EdsmAmountParserTests
{
    private static readonly CultureInfo EnUs = EdsmAmountParser.ResolveCulture("en-US")!;

    [Theory]
    [InlineData("1,250,000.00", 1250000.00)]
    [InlineData("950.00", 950.00)]
    [InlineData("0.00", 0)]
    [InlineData("-500.00", -500.00)]
    [InlineData("-1,234.56", -1234.56)]
    public void HashFormattedValues_InTheConfiguredCulture_AreProvided(string raw, double expected)
    {
        var amount = EdsmAmountParser.Parse(raw, EnUs);

        Assert.Equal(EdsmAmountStatus.Provided, amount.Status);
        Assert.Equal((decimal)expected, amount.Value);
        Assert.Equal(raw, amount.Raw);
    }

    [Theory]
    [InlineData("1250000.00")]   // #,##0.00 always groups thousands
    [InlineData("1,250.5")]      // always two decimals
    [InlineData("1.250,00")]     // another culture's separators
    [InlineData("1,25,000.00")]  // Indian grouping
    [InlineData("AED 100.00")]
    [InlineData("100.00 AED")]   // suffix only where documented
    [InlineData("1e3")]
    [InlineData("(100.00)")]
    [InlineData("100")]
    public void AnythingElse_IsUnreadable_NeverZero(string raw)
    {
        var amount = EdsmAmountParser.Parse(raw, EnUs);

        Assert.Equal(EdsmAmountStatus.Unreadable, amount.Status);
        Assert.Null(amount.Value);
        Assert.Equal(raw, amount.Raw);
    }

    [Fact]
    public void TheAedSuffix_IsStrippedOnlyWhereAllowed()
    {
        Assert.Equal(1000m, EdsmAmountParser.Parse("1,000.00 AED", EnUs, allowAedSuffix: true).Value);
        Assert.Equal(EdsmAmountStatus.Unreadable, EdsmAmountParser.Parse("1,000.00 AED", EnUs).Status);
    }

    [Fact]
    public void WithoutAConfiguredCulture_NothingIsRead_ButBlankAndMissingKeepTheirStatus()
    {
        Assert.Equal(EdsmAmountStatus.FormatNotConfigured, EdsmAmountParser.Parse("1,000.00", null).Status);
        Assert.Null(EdsmAmountParser.Parse("1,000.00", null).Value);
        Assert.Equal(EdsmAmountStatus.Empty, EdsmAmountParser.Parse("", null).Status);
        Assert.Equal(EdsmAmount.Missing, EdsmAmountParser.Parse(null, null));
    }

    [Fact]
    public void AnotherConfiguredCulture_UsesItsOwnSeparators()
    {
        var deDe = EdsmAmountParser.ResolveCulture("de-DE")!;

        Assert.Equal(1250.50m, EdsmAmountParser.Parse("1.250,50", deDe).Value);
        Assert.Equal(EdsmAmountStatus.Unreadable, EdsmAmountParser.Parse("1,250.50", deDe).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("xx-NOPE-1")]
    [InlineData("en-IN")]  // 3;2 grouping: #,##0.00 would not be unambiguous
    public void UnusableCultures_ResolveToNothing(string? name) => Assert.Null(EdsmAmountParser.ResolveCulture(name));

    [Fact]
    public void RawDoubles_AreRoundedToTwoPlaces() => Assert.Equal(1234.56m, EdsmAmountParser.RoundRaw(1234.5600000000001));
}

public sealed class EdsmEnvelopeTests
{
    private static readonly CultureInfo EnUs = EdsmAmountParser.ResolveCulture("en-US")!;

    private static EdsmResult<EdsmPaymentSummary> Summary(int status, string body) =>
        EdsmCollectionsHttpGateway.Interpret(status, body, d => EdsmCollectionsHttpGateway.ReadSummary(d, EnUs));

    [Fact]
    public void TheVerifiedEnvelope_IsRead_CamelCase()
    {
        var result = Summary(200, """{"title":"","status":200,"data":{"totalAmount":"1,250,000.00","paidAmount":"900,000.00","dueAmount":"50,000.00","outstandingAmount":"300,000.00","lateFines":"1,200.00"}}""");

        Assert.Equal(EdsmOutcome.Success, result.Outcome);
        var s = result.Value!;
        Assert.Equal(1_250_000m, s.TotalAmount.Value);
        Assert.Equal(900_000m, s.PaidAmount.Value);
        Assert.Equal(50_000m, s.DueAmount.Value);
        Assert.Equal(300_000m, s.OutstandingAmount.Value);
        Assert.Equal(1_200m, s.LateFines.Value);
    }

    [Theory]
    [InlineData(401, "API Key was not provided.")]
    [InlineData(403, "Unauthorized client.")]
    public void PlainTextAuthenticationErrors_AreUnauthorized_WithTheirText(int status, string text)
    {
        var result = Summary(status, text);

        Assert.Equal(EdsmOutcome.Unauthorized, result.Outcome);
        Assert.Equal(text, result.Title);
        Assert.Null(result.Value);
    }

    [Fact]
    public void ABusinessRule400_IsTheEnvelope_WithItsTitle()
    {
        var result = Summary(400, """{"title":"Company not supported","status":400,"data":null}""");

        Assert.Equal(EdsmOutcome.BusinessRuleRejected, result.Outcome);
        Assert.Equal("Company not supported", result.Title);
    }

    [Fact]
    public void AValidation400_IsProblemDetails_NotTheEnvelope()
    {
        var result = Summary(400, """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"TenantId":["The TenantId field is required."]},"traceId":"00-1"}""");

        Assert.Equal(EdsmOutcome.ValidationRejected, result.Outcome);
        Assert.Contains("TenantId", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(500, "")]
    [InlineData(502, "<html>Bad gateway</html>")]
    [InlineData(404, "")]
    public void ServerErrorsAndUnknownRoutes_AreUnavailable(int status, string body) =>
        Assert.Equal(EdsmOutcome.Unavailable, Summary(status, body).Outcome);

    [Theory]
    [InlineData("""{"totalAmount":"1.00"}""")]                       // bare payload: not the envelope
    [InlineData("""{"title":"","status":200,"data":null}""")]        // 200 without data
    [InlineData("""{"title":"","status":200,"data":[]}""")]          // wrong data shape
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void Unrecognised200s_AreInvalidResponses(string body) =>
        Assert.Equal(EdsmOutcome.InvalidResponse, Summary(200, body).Outcome);

    [Fact]
    public void MissingBlankAndNonStringFields_KeepTheirStatus()
    {
        var s = Summary(200, """{"title":"","status":200,"data":{"totalAmount":"","paidAmount":null,"dueAmount":150,"lateFines":""}}""").Value!;

        Assert.Equal(EdsmAmountStatus.Empty, s.TotalAmount.Status);
        Assert.Equal(EdsmAmountStatus.Missing, s.PaidAmount.Status);
        Assert.Equal(EdsmAmountStatus.Unreadable, s.DueAmount.Status);
        Assert.Equal(EdsmAmountStatus.Missing, s.OutstandingAmount.Status);
        Assert.All([s.TotalAmount, s.PaidAmount, s.DueAmount, s.OutstandingAmount, s.LateFines], a => Assert.Null(a.Value));
    }
}

public sealed class EdsmCollectionsHttpGatewayTests
{
    private const string BaseUrl = "http://pact.example.test:6020/";
    private const string ApiKey = "pact-test-key";

    private static EdsmCollectionsHttpGateway Gateway(StubHttpMessageHandler handler, string? apiKey = ApiKey) =>
        new(new HttpClient(handler) { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(5) },
            Options.Create(new PactApiOptions { BaseUrl = BaseUrl, ApiKey = apiKey }),
            Options.Create(new CollectionsEdsmOptions { EdsmNumberCulture = "en-US" }),
            NullLogger<EdsmCollectionsHttpGateway>.Instance);

    private static StubHttpMessageHandler Answer(HttpStatusCode status, string body, string contentType = "application/json") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, contentType) }));

    private const string SummaryOk = """{"title":"","status":200,"data":{"totalAmount":"1.00","paidAmount":"1.00","dueAmount":"0.00","outstandingAmount":"0.00","lateFines":""}}""";

    [Fact]
    public async Task PaymentSummary_IsTheDocumentedRoute_WithTheApiKeyHeader()
    {
        var handler = Answer(HttpStatusCode.OK, SummaryOk);

        var result = await Gateway(handler).GetPaymentSummaryAsync(4, "3001");

        Assert.Equal(EdsmOutcome.Success, result.Outcome);
        Assert.Equal($"{BaseUrl}v1/reports/payment-summary?TenantId=3001&CompanyId=4", handler.LastRequest!.RequestUri!.AbsoluteUri);
        Assert.Equal(ApiKey, Assert.Single(handler.LastRequest.Headers.GetValues("X-API-KEY")));
    }

    [Fact]
    public async Task PlainText403_FromTheWire_IsUnauthorized()
    {
        var result = await Gateway(Answer(HttpStatusCode.Forbidden, "Unauthorized client.", "text/plain")).GetPaymentSummaryAsync(4, "3001");

        Assert.Equal(EdsmOutcome.Unauthorized, result.Outcome);
        Assert.Equal("Unauthorized client.", result.Title);
    }

    [Fact]
    public async Task AnUnknownCompany_IsNotSupported_WithoutACall()
    {
        var handler = Answer(HttpStatusCode.OK, SummaryOk);

        var result = await Gateway(handler).GetPaymentSummaryAsync(99, "3001");

        Assert.Equal(EdsmOutcome.NotSupported, result.Outcome);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task NoApiKey_IsUnavailable_WithoutACall()
    {
        var handler = Answer(HttpStatusCode.OK, SummaryOk);

        Assert.Equal(EdsmOutcome.Unavailable, (await Gateway(handler, apiKey: " ").GetPaymentSummaryAsync(4, "3001")).Outcome);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ATimeoutOrNetworkFailure_IsUnavailable()
    {
        var timeout = new StubHttpMessageHandler((_, _) => throw new TaskCanceledException("timeout"));
        var network = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("refused"));

        Assert.Equal(EdsmOutcome.Unavailable, (await Gateway(timeout).GetPaymentSummaryAsync(4, "3001")).Outcome);
        Assert.Equal(EdsmOutcome.Unavailable, (await Gateway(network).GetPaymentSummaryAsync(4, "3001")).Outcome);
    }

    [Fact]
    public async Task PaymentTransactions_SendsAllFourParameters_AndReadsRentedDueAedSuffix()
    {
        var handler = Answer(HttpStatusCode.OK, """
            {"title":"","status":200,"data":{"totalAmount":1000.0,"formattedTotalAmount":"1,000.00","transactions":[
              {"amount":1000.0000000000001,"formattedAmount":"1,000.00 AED","date":"01-Mar-2026","chequeNumber":null,"transactionTypeId":2,"paymentTypeId":3},
              {"amount":0.0,"formattedAmount":"0.00","date":"","chequeNumber":null,"transactionTypeId":2,"paymentTypeId":4}]}}
            """);

        var result = await Gateway(handler).GetPaymentTransactionsAsync(25, "3001", "971500000002", EdsmTransactionType.Due);

        Assert.Equal($"{BaseUrl}v1/reports/payment-transactions?Mobile=971500000002&TenantId=3001&CompanyId=25&TransactionTypeId=2",
            handler.LastRequest!.RequestUri!.AbsoluteUri);
        var rows = result.Value!.Items;
        Assert.Equal(1000m, rows[0].Amount);
        Assert.Equal(1000m, rows[0].FormattedAmount.Value);
        Assert.Equal(new DateOnly(2026, 3, 1), rows[0].Date);
        Assert.Equal(3, rows[0].PaymentTypeId);
        Assert.Null(rows[1].Date);                     // "" for opening-balance rows
    }

    [Fact]
    public async Task OwnedTransactions_DoNotAcceptTheAedSuffix()
    {
        var handler = Answer(HttpStatusCode.OK, """{"title":"","status":200,"data":{"transactions":[{"amount":1.0,"formattedAmount":"1.00 AED","date":"","transactionTypeId":2}]}}""");

        var result = await Gateway(handler).GetPaymentTransactionsAsync(4, "3001", "971500000002", EdsmTransactionType.Due);

        Assert.Equal(EdsmAmountStatus.Unreadable, Assert.Single(result.Value!.Items).FormattedAmount.Status);
    }

    [Fact]
    public async Task TransactionTypeAll_IsNeverSent_BecauseItWritesToEdsm()
    {
        var handler = Answer(HttpStatusCode.OK, "{}");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Gateway(handler).GetPaymentTransactionsAsync(25, "3001", "971500000002", (EdsmTransactionType)4));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task DueInstallments_IsTheDocumentedRoute_AndReadsRowsAsSent()
    {
        var handler = Answer(HttpStatusCode.OK, """
            {"title":"","status":200,"data":[{"companyID":4,"tenantID":12345,"unitID":678,"voucherNumber":"PDC-0001 ","chequeNumber":null,"chequeDueDate":"2026-10-15T00:00:00","amount":25000.000000000004,"status":" Due"}]}
            """);

        var result = await Gateway(handler).GetDueInstallmentsAsync(4, new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 31));

        Assert.Equal($"{BaseUrl}v1/due-installments?CompanyId=4&FromDate=2026-09-01&ToDate=2026-10-31", handler.LastRequest!.RequestUri!.AbsoluteUri);
        var row = Assert.Single(result.Value!);
        Assert.Equal(12345, row.TenantId);
        Assert.Equal(new DateOnly(2026, 10, 15), row.ChequeDueDate);
        Assert.Equal(25000m, row.Amount);
        Assert.Equal("PDC-0001 ", row.VoucherNumber);   // raw, as EDSM sends it
        Assert.Equal(" Due", row.Status);                // raw: meaning UNVERIFIED
        Assert.Null(row.ChequeNumber);
    }

    [Fact]
    public async Task DueInstallments_ForCompany20_IsNotSupported_WithoutACall()
    {
        var handler = Answer(HttpStatusCode.OK, "{}");

        var result = await Gateway(handler).GetDueInstallmentsAsync(20, new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 31));

        Assert.Equal(EdsmOutcome.NotSupported, result.Outcome);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task PactContractsLookup_CarriesEachRowsCompanyId()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"data":[
                  {"tenantID":3001,"companyID":4,"unitID":41230,"unitNumber":"0304","contractID":88001,"projectName":"Tiger Marina Residences","customerBuyerType":2},
                  {"tenantID":3001,"companyID":25,"unitID":51200,"unitNumber":"1101","contractID":99002,"projectName":"Hirmas Residence","customerBuyerType":2},
                  {"tenantID":3001,"unitID":61200,"unitNumber":"0201","contractID":99003,"customerBuyerType":2}
                ]}
                """, System.Text.Encoding.UTF8, "application/json")
        }));
        var gateway = new PactCustomerHttpGateway(new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) },
            Options.Create(new PactApiOptions { BaseUrl = BaseUrl, ApiKey = ApiKey }), NullLogger<PactCustomerHttpGateway>.Instance);

        var result = await gateway.SearchByMobileAsync("971500000002");

        Assert.Equal([4, 25, null], Assert.Single(result.Customers!).Contracts.Select(c => c.CompanyId));
    }
}

public sealed class CollectionsPaymentSummaryAppServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);
    private const string PactKey = "ext:Pact:3001";
    private const string Phone = "+971500000002";

    private readonly CollectionsOptions _options = new() { Enabled = true };
    private readonly CollectionsEdsmOptions _edsmOptions = new() { EdsmNumberCulture = "en-US" };
    private readonly FakeProfiles _profiles = new();
    private readonly FakePactCustomerLookupGateway _pact = new();
    private readonly RecordingEdsm _edsm;
    private readonly CollectionsCaller _agent = new(Guid.NewGuid(), [Roles.CsAgent], []);

    public CollectionsPaymentSummaryAppServiceTests() => _edsm = new RecordingEdsm(new FixtureEdsmCollectionsGateway(Options.Create(_edsmOptions)));

    private readonly FakeTimeProvider _time = new(Now);
    private PactAccountMappingCache? _cache;

    /// <summary>A fresh service per call, like a request scope; the mapping cache is shared, like the singleton.</summary>
    private CollectionsPaymentSummaryAppService Service() => new(
        _options, _edsmOptions,
        new CollectionsAuthorizationService(_options, new FakeDepartmentRepository()),
        new CollectionsClock(_options, _time),
        _profiles, _pact, _edsm, _cache ??= new PactAccountMappingCache(_time));

    private static CustomerDirectoryProfileDto Profile(string key, int? crmId, string? source, string? externalId, params string[] phones) =>
        new(key, crmId is null ? (source is null ? "Phone" : "External") : "Crm", "Customer", phones, [], source ?? (crmId is null ? "Unverified" : "Crm"),
            crmId, source, externalId, 0, 1, Now.AddDays(-10), Now.AddDays(-1), 1, [], [], []);

    private static PactCustomerMatchDto Tenant(string tenantId, params PactContractDto[] contracts) => new(tenantId, "Customer", Phone, null, "2", contracts);

    private static readonly PactContractDto Owned = new("41230", "88001", "0304", "Tiger Marina Residences", "Residential", 4);
    private static readonly PactContractDto Rented = new("51200", "99002", "1101", "Hirmas Residence", "Residential", 25);
    private static readonly PactContractDto OwnedParking = new("41299", "88050", "P-12", "Tiger Marina Residences", "Parking", 4);

    private void SeedPactCustomer(params PactContractDto[] contracts)
    {
        _profiles.Add(Profile(PactKey, null, "Pact", "3001", Phone));
        _pact.Seed(Phone, Tenant("3001", contracts));
    }

    [Fact]
    public async Task OwnedAndRentedCompanies_AreMappedSeparately_FromThePactPairs()
    {
        SeedPactCustomer(Owned, OwnedParking, Rented);
        _pact.Seed(Phone, Tenant("4444", new PactContractDto("70000", "77777", "0101", "Other", "Residential", 32)));

        var dto = (await Service().GetAsync(_agent, PactKey)).Value!;

        Assert.Equal("Mapped", dto.MappingStatus);
        Assert.Equal([(4, "3001"), (25, "3001")], _edsm.SummaryCalls);    // never tenant 4444 / company 32
        Assert.Null(dto.SourceAsOfUtc);
        Assert.Equal("AED", dto.Currency);
        Assert.Equal("Configured", dto.CurrencySource);
        Assert.Equal(20, dto.MaxSourceDelayMinutes);

        var owned = dto.Companies[0];
        Assert.Equal("Owned", owned.BusinessModel);
        Assert.Equal(2, owned.Contracts.Count);                          // unit + parking, one figure set per company and tenant
        Assert.Equal("Not yet due", owned.Fields.Single(f => f.Key == "outstandingAmount").Label);
        Assert.Equal(1_500m, owned.Fields.Single(f => f.Key == "lateFines").Value);
        Assert.Contains("Excludes late fines", owned.Fields.Single(f => f.Key == "totalAmount").Definition, StringComparison.Ordinal);
        Assert.Equal("Consistent", owned.TotalCheck);
        Assert.Equal(["Paid", "Due", "Outstanding"], owned.Transactions.Select(t => t.TransactionType));
        Assert.All(owned.Transactions, t => Assert.Null(t.Caveat));

        var rented = dto.Companies[1];
        Assert.Equal("Rented", rented.BusinessModel);
        Assert.Equal("Post-dated cheques", rented.Fields.Single(f => f.Key == "outstandingAmount").Label);
        Assert.Equal(-500m, rented.Fields.Single(f => f.Key == "dueAmount").Value);   // documented: can be negative
        var fines = rented.Fields.Single(f => f.Key == "lateFines");
        Assert.Equal("Empty", fines.Status);
        Assert.Equal("NotComputedForRented", fines.Meaning);
        Assert.Null(fines.Value);
        Assert.NotNull(rented.Transactions.Single(t => t.TransactionType == "Paid").Caveat);
        Assert.NotNull(rented.Transactions.Single(t => t.TransactionType == "Due").Caveat);
        var fee = rented.Transactions.Single(t => t.TransactionType == "Due").Items.Single();
        Assert.Equal(1000m, fee.Amount);
        Assert.Equal(3, fee.PaymentTypeId);
        Assert.Equal("Fees", fee.PaymentType);
        Assert.All(owned.Transactions.SelectMany(t => t.Items), i => { Assert.Null(i.PaymentTypeId); Assert.Null(i.PaymentType); });
    }

    [Fact]
    public async Task ExpiredContractsStillEstablishHistoricalPaymentMapping()
    {
        SeedPactCustomer(Owned with { ContractEndDate = new DateOnly(2000, 1, 1) }, Rented with { ContractEndDate = new DateOnly(2001, 1, 1) });
        var summary = (await Service().GetAsync(_agent, PactKey)).Value!;
        Assert.Equal("Mapped", summary.MappingStatus);
        Assert.Equal([4, 25], summary.Companies.Select(c => c.CompanyId));
        Assert.Equal(2, summary.Companies.Sum(c => c.Contracts.Count));
    }

    [Fact]
    public async Task OnlyReadOnlyTransactionTypes_AreRequested()
    {
        SeedPactCustomer(Owned, Rented);

        await Service().GetAsync(_agent, PactKey);

        Assert.All(_edsm.TransactionTypes, t => Assert.InRange((int)t, 1, 3));
        Assert.Equal(6, _edsm.TransactionTypes.Count);
    }

    [Fact]
    public async Task AnOwnedBlankLateFine_MeansZeroOrLess()
    {
        _edsm.SummaryOverride[(4, "3001")] = """{"title":"","status":200,"data":{"totalAmount":"10.00","paidAmount":"10.00","dueAmount":"0.00","outstandingAmount":"0.00","lateFines":""}}""";
        SeedPactCustomer(Owned);

        var fines = (await Service().GetAsync(_agent, PactKey)).Value!.Companies.Single().Fields.Single(f => f.Key == "lateFines");

        Assert.Equal("ZeroOrLess", fines.Meaning);
        Assert.Null(fines.Value);
    }

    [Fact]
    public async Task AllZeros_AreNotTreatedAsProofOfAnAccount()
    {
        _edsm.SummaryOverride[(4, "3001")] = """{"title":"","status":200,"data":{"totalAmount":"0.00","paidAmount":"0.00","dueAmount":"0.00","outstandingAmount":"0.00","lateFines":""}}""";
        SeedPactCustomer(Owned);

        var company = (await Service().GetAsync(_agent, PactKey)).Value!.Companies.Single();

        Assert.True(company.AllZero);
        Assert.Contains(company.Notes, n => n.Contains("confirmed through PACT contracts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnInconsistentTotal_IsFlagged()
    {
        _edsm.SummaryOverride[(4, "3001")] = """{"title":"","status":200,"data":{"totalAmount":"99.00","paidAmount":"10.00","dueAmount":"1.00","outstandingAmount":"1.00","lateFines":""}}""";
        SeedPactCustomer(Owned);

        var company = (await Service().GetAsync(_agent, PactKey)).Value!.Companies.Single();

        Assert.Equal("Inconsistent", company.TotalCheck);
        Assert.NotEmpty(company.Notes);
    }

    [Fact]
    public async Task WithoutAConfiguredCulture_NoFormattedAmountIsRead()
    {
        _edsmOptions.EdsmNumberCulture = null;
        SeedPactCustomer(Owned);

        var company = (await Service().GetAsync(_agent, PactKey)).Value!.Companies.Single();

        Assert.All(company.Fields.Where(f => f.Raw is { Length: > 0 }), f => Assert.Equal("FormatNotConfigured", f.Status));
        Assert.All(company.Fields, f => Assert.Null(f.Value));
        Assert.Equal("NotChecked", company.TotalCheck);
    }

    [Fact]
    public async Task AnUnknownCompany_IsReported_AndEdsmIsNotAsked()
    {
        SeedPactCustomer(new PactContractDto("1", "2", "3", "X", "Residential", 99));

        var company = (await Service().GetAsync(_agent, PactKey)).Value!.Companies.Single();

        Assert.Equal("NotSupported", company.Status);
        Assert.Empty(_edsm.SummaryCalls);
    }

    [Fact]
    public async Task DueInstallments_AreOffByDefault()
    {
        SeedPactCustomer(Owned);

        var due = (await Service().GetAsync(_agent, PactKey)).Value!.Companies.Single().DueInstallments!;

        Assert.Equal("Disabled", due.Status);
        Assert.Empty(_edsm.DueCalls);
    }

    [Fact]
    public async Task DueInstallments_WhenEnabled_AreFilteredToThisTenantAndCompany()
    {
        _edsmOptions.DueInstallmentsEnabled = true;
        SeedPactCustomer(Owned);

        var due = (await Service().GetAsync(_agent, PactKey)).Value!.Companies.Single().DueInstallments!;

        Assert.Equal("Available", due.Status);
        Assert.Equal(new DateOnly(2026, 9, 4), due.FromDate);
        Assert.Equal(new DateOnly(2026, 11, 5), due.ToDate);
        Assert.Equal(2, due.Items.Count);                                // the fixture's tenant 9999 row is dropped
        Assert.Equal("PDC-0412", due.Items[0].VoucherNumber);            // trimmed for display
        Assert.Equal("Due ", due.Items[0].SourceStatus);                 // status passed through raw
        Assert.Equal(187_500m, due.Items[1].Amount);                     // 187500.00000000003 rounded
    }

    [Fact]
    public async Task DueInstallments_ForCompany20_AreNotSupported_AndNotCalled()
    {
        _edsmOptions.DueInstallmentsEnabled = true;
        SeedPactCustomer(new PactContractDto("61200", "99100", "0101", "Trio 3", "Residential", 20));

        var due = (await Service().GetAsync(_agent, PactKey)).Value!.Companies.Single().DueInstallments!;

        Assert.Equal("NotSupported", due.Status);
        Assert.Empty(_edsm.DueCalls);
    }

    [Fact]
    public async Task ACrmCustomer_IsNotMapped_AndNeitherPactNorEdsmIsCalled()
    {
        _profiles.Add(Profile("crm:9001", 9001, null, null, Phone));

        var result = await Service().GetAsync(_agent, "crm:9001");

        Assert.Equal("NotMapped", result.Value!.MappingStatus);
        Assert.Contains("Tiger CRM (customerId 9001)", result.Value.MappingDetail, StringComparison.Ordinal);
        Assert.Equal(0, _pact.SearchCallCount);
        Assert.Empty(_edsm.SummaryCalls);
    }

    // ---- next payment on the summary contract (fixture tests; see CollectionsNextPaymentServiceTests) ----

    [Fact]
    public async Task ACrmCustomer_GetsAnExplicitNextPaymentUnavailable_WithNoGuessedTenantOrCompany()
    {
        _profiles.Add(Profile("crm:9001", 9001, null, null, Phone));

        var dto = (await Service().GetAsync(_agent, "crm:9001")).Value!;

        Assert.Equal("NotMapped", dto.MappingStatus);
        Assert.Equal("Unavailable", dto.NextPayment!.Status);
        Assert.Equal(["MappingNotAvailable"], dto.NextPayment.Reasons);
        Assert.Empty(dto.NextPayment.Companies);
        Assert.Empty(_edsm.DueCalls);
    }

    [Fact]
    public async Task AMappedCustomer_ByDefault_HasNextPaymentUnavailable_AndDueInstallmentsIsNeverCalled()
    {
        SeedPactCustomer(Owned, Rented);

        var dto = (await Service().GetAsync(_agent, PactKey)).Value!;

        Assert.Equal("Mapped", dto.MappingStatus);
        Assert.Equal("Unavailable", dto.NextPayment!.Status);
        Assert.Equal(["FeatureDisabled"], dto.NextPayment.Reasons);
        Assert.False(dto.NextPayment.IsComplete);
        Assert.Empty(_edsm.DueCalls);                         // DueInstallmentsEnabled is off and stays off
        Assert.Equal("Disabled", dto.Companies[0].DueInstallments!.Status);
    }

    [Fact]
    public async Task EnabledWithoutConfirmedSemantics_NamesWhatIsMissing_PerMappedCompany_WithoutCallingEdsm()
    {
        SeedPactCustomer(Owned, Rented);
        _edsmOptions.NextPayment.Enabled = true;

        var next = (await Service().GetAsync(_agent, PactKey)).Value!.NextPayment!;

        Assert.Equal("Unavailable", next.Status);
        Assert.Equal([4, 25], next.Companies.Select(c => c.CompanyId));
        Assert.All(next.Companies, c => Assert.Contains("UnpaidStatusValues", c.MissingSemantics));
        Assert.Empty(_edsm.DueCalls);
    }

    [Fact]
    public async Task ATenantPactNoLongerReturns_IsNotMapped_EvenThoughEdsmWouldAnswerWithZeros()
    {
        _profiles.Add(Profile(PactKey, null, "Pact", "3001", Phone));
        _pact.Seed(Phone, Tenant("4444", new PactContractDto("70000", "77777", "0101", "Other", "Residential", 4)));

        var result = await Service().GetAsync(_agent, PactKey);

        Assert.Equal("NotMapped", result.Value!.MappingStatus);
        Assert.Empty(_edsm.SummaryCalls);
    }

    [Fact]
    public async Task ContractsWithoutACompany_AreReported_NotGuessed()
    {
        SeedPactCustomer(new PactContractDto("41230", "88001", "0304", "Tiger Marina Residences", "Residential"));

        var result = await Service().GetAsync(_agent, PactKey);

        Assert.Equal("NotMapped", result.Value!.MappingStatus);
        Assert.Single(result.Value.ContractsWithoutCompany);
        Assert.Empty(_edsm.SummaryCalls);
    }

    [Fact]
    public async Task PactUnreachable_IsFinanceUnavailable()
    {
        _profiles.Add(Profile(PactKey, null, "Pact", "3001", Phone));
        _pact.ForcedOutcome = PactCustomerLookupOutcome.Unavailable;

        Assert.Equal(CollectionsOutcome.FinanceUnavailable, (await Service().GetAsync(_agent, PactKey)).Outcome);
        Assert.Empty(_edsm.SummaryCalls);
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

    // ---- Contract discovery (v1/contracts/{mobile} writes inside EDSM) ----

    [Fact]
    public async Task RepeatedTabLoads_DiscoverContractsOnce_AndReuseTheVerifiedMapping()
    {
        SeedPactCustomer(Owned, Rented);

        var first = (await Service().GetAsync(_agent, PactKey)).Value!;
        for (var load = 0; load < 4; load++)
        {
            _time.Advance(TimeSpan.FromMinutes(5));
            var again = (await Service().GetAsync(_agent, PactKey)).Value!;
            Assert.Equal("Cached", again.MappingSource);
            Assert.Equal(first.MappingVerifiedAtUtc, again.MappingVerifiedAtUtc);
            Assert.Equal("Mapped", again.MappingStatus);
        }

        Assert.Equal("PactLookup", first.MappingSource);
        Assert.Equal(Now, first.MappingVerifiedAtUtc);
        Assert.Equal(1, _pact.SearchCallCount);                  // one discovery for five loads
        Assert.Equal(10, _edsm.SummaryCalls.Count);              // EDSM's read-only figures are still read each load
    }

    [Fact]
    public async Task TheMapping_IsRevalidatedAfterItsTtl()
    {
        SeedPactCustomer(Owned);
        await Service().GetAsync(_agent, PactKey);

        _time.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal("Cached", (await Service().GetAsync(_agent, PactKey)).Value!.MappingSource);
        _time.Advance(TimeSpan.FromMinutes(2));
        var after = (await Service().GetAsync(_agent, PactKey)).Value!;

        Assert.Equal("PactLookup", after.MappingSource);
        Assert.Equal(Now.AddMinutes(31), after.MappingVerifiedAtUtc);
        Assert.Equal(2, _pact.SearchCallCount);
    }

    [Fact]
    public async Task AChangedPhoneNumber_TriggersDiscovery()
    {
        SeedPactCustomer(Owned);
        await Service().GetAsync(_agent, PactKey);

        _profiles.Add(Profile(PactKey, null, "Pact", "3001", Phone, "+971500000003"));
        var after = (await Service().GetAsync(_agent, PactKey)).Value!;

        Assert.Equal("PactLookup", after.MappingSource);
        Assert.Equal(3, _pact.SearchCallCount);                  // 1 + both numbers
    }

    [Fact]
    public async Task APactFailure_IsNeverCached_SoTheRetryRediscovers()
    {
        SeedPactCustomer(Owned);
        _pact.ForcedOutcome = PactCustomerLookupOutcome.Unavailable;
        Assert.Equal(CollectionsOutcome.FinanceUnavailable, (await Service().GetAsync(_agent, PactKey)).Outcome);

        _pact.ForcedOutcome = null;
        var retry = (await Service().GetAsync(_agent, PactKey)).Value!;

        Assert.Equal("PactLookup", retry.MappingSource);
        Assert.Equal("Mapped", retry.MappingStatus);
        Assert.Equal(2, _pact.SearchCallCount);
    }

    [Fact]
    public async Task NoContractsForTheTenant_IsReusedBriefly_ThenRechecked()
    {
        _profiles.Add(Profile(PactKey, null, "Pact", "3001", Phone));
        _pact.Seed(Phone, Tenant("4444", Owned));

        await Service().GetAsync(_agent, PactKey);
        var retry = (await Service().GetAsync(_agent, PactKey)).Value!;
        Assert.Equal("NotMapped", retry.MappingStatus);
        Assert.Equal(1, _pact.SearchCallCount);

        _time.Advance(TimeSpan.FromMinutes(6));
        await Service().GetAsync(_agent, PactKey);
        Assert.Equal(2, _pact.SearchCallCount);
    }

    [Fact]
    public async Task AnEdsmRejectionOfACachedPair_InvalidatesTheMapping()
    {
        SeedPactCustomer(Owned);
        _edsm.SummaryOverride[(4, "3001")] = """{"title":"Company not supported","status":400,"data":null}""";

        var first = (await Service().GetAsync(_agent, PactKey)).Value!;
        var second = (await Service().GetAsync(_agent, PactKey)).Value!;

        Assert.Equal("BusinessRuleRejected", first.Companies.Single().Status);
        Assert.Equal("PactLookup", second.MappingSource);
        Assert.Equal(2, _pact.SearchCallCount);
    }

    [Fact]
    public async Task ACachedMapping_NeverBypassesAuthorizationOrVisibility()
    {
        SeedPactCustomer(Owned);
        await Service().GetAsync(_agent, PactKey);

        Assert.Equal(CollectionsOutcome.Forbidden,
            (await Service().GetAsync(new CollectionsCaller(Guid.NewGuid(), [Roles.ReportingUser], []), PactKey)).Outcome);
        _profiles.Remove(PactKey);                                // e.g. the caller can no longer see the customer
        Assert.Equal(CollectionsOutcome.AccountNotFound, (await Service().GetAsync(_agent, PactKey)).Outcome);
        Assert.Equal(1, _pact.SearchCallCount);
    }

    private sealed class FakeProfiles : ICollectionsCustomerProfiles
    {
        private readonly Dictionary<string, CustomerDirectoryProfileDto> _profiles = [];

        public void Add(CustomerDirectoryProfileDto profile) => _profiles[profile.CustomerKey] = profile;

        public void Remove(string key) => _profiles.Remove(key);

        public Task<CustomerDirectoryProfileResult> GetProfileAsync(
            Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, string customerKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(!customerKey.Contains(':', StringComparison.Ordinal)
                ? CustomerDirectoryProfileResult.Failure(CustomerDirectoryProfileOutcome.InvalidKey)
                : _profiles.TryGetValue(customerKey, out var profile)
                    ? CustomerDirectoryProfileResult.Success(profile)
                    : CustomerDirectoryProfileResult.Failure(CustomerDirectoryProfileOutcome.NotFound));
    }

    /// <summary>The fixture gateway (real wire bodies through the real reader), recording every call.</summary>
    private sealed class RecordingEdsm(FixtureEdsmCollectionsGateway inner) : IEdsmCollectionsGateway
    {
        public List<(int, string)> SummaryCalls { get; } = [];
        public List<EdsmTransactionType> TransactionTypes { get; } = [];
        public List<int> DueCalls { get; } = [];
        public Dictionary<(int, string), string> SummaryOverride { get; } = [];
        public string SourceName => "Test";

        public Task<EdsmResult<EdsmPaymentSummary>> GetPaymentSummaryAsync(int companyId, string tenantId, CancellationToken cancellationToken = default)
        {
            SummaryCalls.Add((companyId, tenantId));
            return SummaryOverride.TryGetValue((companyId, tenantId), out var body)
                ? Task.FromResult(EdsmCollectionsHttpGateway.Interpret(200, body, d => EdsmCollectionsHttpGateway.ReadSummary(d, EdsmAmountParser.ResolveCulture("en-US"))))
                : inner.GetPaymentSummaryAsync(companyId, tenantId, cancellationToken);
        }

        public Task<EdsmResult<EdsmPaymentTransactions>> GetPaymentTransactionsAsync(
            int companyId, string tenantId, string mobile, EdsmTransactionType type, CancellationToken cancellationToken = default)
        {
            TransactionTypes.Add(type);
            return inner.GetPaymentTransactionsAsync(companyId, tenantId, mobile, type, cancellationToken);
        }

        public Task<EdsmResult<IReadOnlyList<EdsmDueInstallment>>> GetDueInstallmentsAsync(
            int companyId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default)
        {
            DueCalls.Add(companyId);
            return inner.GetDueInstallmentsAsync(companyId, fromDate, toDate, cancellationToken);
        }
    }
}

public sealed class EdsmPaymentTypeMappingTests
{
    [Theory]
    [InlineData(1, "Cash")]
    [InlineData(2, "Cheque")]
    [InlineData(3, "Fees")]
    [InlineData(4, "Opening balance")]
    [InlineData(5, "Current contract amount")]
    [InlineData(9, "Unknown (9)")]
    [InlineData(null, null)]
    public void PaymentTypeId_MapsToTheDocumentedPaymentTypeEnumName(int? id, string? expected) =>
        Assert.Equal(expected, CollectionsPaymentSummaryAppService.PaymentTypeName(id));
}

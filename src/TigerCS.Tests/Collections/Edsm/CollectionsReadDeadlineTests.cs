using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Integrations.Modules.CollectionsIntegration;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Edsm;

/// <summary>
/// The overall read deadline (PACT discovery and every EDSM call together),
/// caller cancellation, and the completeness of what is returned. Slow sources
/// are fakes that wait on the token, so every test finishes in well under a second.
/// Fixture EDSM data only: nothing here says anything about real EDSM timings.
/// </summary>
public sealed class CollectionsReadDeadlineTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);
    private const string PactKey = "ext:Pact:3001";
    private const string Phone = "+971500000002";
    private const string OtherPhone = "+971500000003";
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    private static readonly PactContractDto Owned = new("41230", "88001", "0304", "Tiger Marina Residences", "Residential", 4);
    private static readonly PactContractDto Rented = new("51200", "99002", "1101", "Hirmas Residence", "Residential", 25);

    private readonly CollectionsOptions _options = new() { Enabled = true };
    private readonly CollectionsEdsmOptions _edsmOptions = new() { EdsmNumberCulture = "en-US" };
    private readonly Profiles _profiles = new();
    private readonly ScriptedPact _pact = new();
    private readonly ScriptedEdsm _edsm;
    private readonly FakeTimeProvider _time = new(Now);
    private readonly CollectionsCaller _agent = new(Guid.NewGuid(), [Roles.CsAgent], []);
    private PactAccountMappingCache? _cache;

    public CollectionsReadDeadlineTests() => _edsm = new ScriptedEdsm(new FixtureEdsmCollectionsGateway(Options.Create(_edsmOptions)));

    private CollectionsPaymentSummaryAppService Service() => new(
        _options, _edsmOptions,
        new CollectionsAuthorizationService(_options, new FakeDepartmentRepository()),
        new CollectionsClock(_options, _time),
        _profiles, _pact, _edsm, _cache ??= new PactAccountMappingCache(_time));

    private void SeedCustomer(params string[] phones)
    {
        _profiles.Profile = new(PactKey, "External", "Customer", phones, [], "Pact", null, "Pact", "3001", 0, 1, Now.AddDays(-10), Now.AddDays(-1), 1, [], [], []);
    }

    private static PactCustomerLookupResult Found(params PactContractDto[] contracts) =>
        PactCustomerLookupResult.Success([new PactCustomerMatchDto("3001", "Customer", Phone, null, "2", contracts)]);

    private static void AssertNoFigures(CollectionsCompanyPaymentSummaryDto company)
    {
        Assert.Empty(company.Fields);           // never a substituted zero
        Assert.False(company.AllZero);
        Assert.Empty(company.Transactions);
    }

    // ---- 1. Slow discovery ----

    [Fact]
    public async Task SlowPactDiscovery_PastTheDeadline_Is503_AndCachesNothing()
    {
        SeedCustomer(Phone);
        _pact.Hang(Phone);

        var result = await Service().GetAsync(_agent, PactKey, includeTransactions: false, deadline: Short);

        Assert.Equal(CollectionsOutcome.FinanceUnavailable, result.Outcome);
        Assert.Contains("deadline passed before the customer's accounts were confirmed", result.Detail);
        Assert.True(_pact.SawCancellation);
        Assert.Empty(_edsm.SummaryCalls);

        // Nothing was cached: the next request asks PACT again and gets the real answer.
        _pact.Answer(Phone, Found(Owned));
        var next = await Service().GetAsync(_agent, PactKey, includeTransactions: false, deadline: Long);
        Assert.Equal(CollectionsOutcome.Success, next.Outcome);
        Assert.Equal("PactLookup", next.Value!.MappingSource);
        Assert.Equal(2, _pact.Calls);
    }

    // ---- 2. Multiple companies, one slow ----

    [Fact]
    public async Task MultipleCompanies_OneSlow_KeepsTheOthersFigures_AndMarksTheResponsePartial()
    {
        SeedCustomer(Phone);
        _pact.Answer(Phone, Found(Owned, Rented));
        _edsm.HangSummaryFor(25);

        var started = DateTime.UtcNow;
        var dto = (await Service().GetAsync(_agent, PactKey, includeTransactions: false, deadline: Short)).Value!;

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Equal(CollectionsCompleteness.Partial, dto.Completeness);

        var owned = Assert.Single(dto.Companies, c => c.CompanyId == 4);
        Assert.Equal("Available", owned.Status);
        Assert.NotEmpty(owned.Fields);

        var rented = Assert.Single(dto.Companies, c => c.CompanyId == 25);
        Assert.Equal(CollectionsCompleteness.DeadlineExceeded, rented.Status);
        Assert.Contains("deadline passed", rented.StatusDetail);
        AssertNoFigures(rented);

        var reason = Assert.Single(dto.IncompleteReasons!);
        Assert.StartsWith("Company 25", reason);
    }

    [Fact]
    public async Task MultipleCompanies_AllAnswered_IsComplete()
    {
        SeedCustomer(Phone);
        _pact.Answer(Phone, Found(Owned, Rented));

        var dto = (await Service().GetAsync(_agent, PactKey, includeTransactions: false, deadline: Long)).Value!;

        Assert.Equal(CollectionsCompleteness.Complete, dto.Completeness);
        Assert.Empty(dto.IncompleteReasons!);
        Assert.All(dto.Companies, c => Assert.Equal("Available", c.Status));
    }

    // ---- 3. Source timeouts (the gateway's own per-call timeout) ----

    [Fact]
    public async Task EdsmPerCallTimeout_ForOneCompany_IsUnavailable_WithoutFigures_AndPartial()
    {
        SeedCustomer(Phone);
        _pact.Answer(Phone, Found(Owned, Rented));
        _edsm.FailSummaryFor(25, EdsmOutcome.Unavailable, "EDSM request timed out.");

        var dto = (await Service().GetAsync(_agent, PactKey, includeTransactions: false, deadline: Long)).Value!;

        var rented = Assert.Single(dto.Companies, c => c.CompanyId == 25);
        Assert.Equal("Unavailable", rented.Status);
        AssertNoFigures(rented);
        Assert.Equal(CollectionsCompleteness.Partial, dto.Completeness);
        Assert.Contains(dto.IncompleteReasons!, r => r.Contains("Company 25") && r.Contains("Unavailable"));
    }

    [Fact]
    public async Task EveryCompanyFailing_IsNoFigures_NeverComplete()
    {
        SeedCustomer(Phone);
        _pact.Answer(Phone, Found(Owned, Rented));
        _edsm.FailSummaryFor(4, EdsmOutcome.Unavailable, "timed out");
        _edsm.FailSummaryFor(25, EdsmOutcome.Unavailable, "timed out");

        var dto = (await Service().GetAsync(_agent, PactKey, includeTransactions: false, deadline: Long)).Value!;

        Assert.Equal(CollectionsCompleteness.NoFigures, dto.Completeness);
        Assert.All(dto.Companies, AssertNoFigures);
    }

    [Fact]
    public async Task PactPerCallTimeout_OnOneOfTwoNumbers_IsPartial_AndNotCached()
    {
        SeedCustomer(Phone, OtherPhone);
        _pact.Answer(Phone, Found(Owned));
        _pact.Answer(OtherPhone, PactCustomerLookupResult.Unavailable("PACT request timed out."));

        var dto = (await Service().GetAsync(_agent, PactKey, includeTransactions: false, deadline: Long)).Value!;

        Assert.Equal("Mapped", dto.MappingStatus);
        Assert.Equal("PactLookupPartial", dto.MappingSource);
        Assert.Equal(CollectionsCompleteness.Partial, dto.Completeness);
        Assert.Contains(dto.IncompleteReasons!, r => r.Contains("did not answer for 1 of the customer's 2 phone number(s)"));

        await Service().GetAsync(_agent, PactKey, includeTransactions: false, deadline: Long);
        Assert.Equal(4, _pact.Calls);   // rediscovered: the partial mapping was never cached
    }

    [Fact]
    public async Task PactPerCallTimeout_WithNoContractsFound_Is503_NeverNotMapped()
    {
        SeedCustomer(Phone, OtherPhone);
        _pact.Answer(Phone, PactCustomerLookupResult.NotFound());
        _pact.Answer(OtherPhone, PactCustomerLookupResult.Unavailable("PACT request timed out."));

        var result = await Service().GetAsync(_agent, PactKey, includeTransactions: false, deadline: Long);

        Assert.Equal(CollectionsOutcome.FinanceUnavailable, result.Outcome);
        Assert.Contains("could not be confirmed", result.Detail);
    }

    // ---- includeTransactions ----

    [Fact]
    public async Task GenesysLightSummary_MakesNoTransactionCalls_AndIsComplete()
    {
        SeedCustomer(Phone);
        _pact.Answer(Phone, Found(Owned, Rented));

        var dto = (await Service().GetAsync(_agent, PactKey, includeTransactions: false, deadline: Long)).Value!;

        Assert.Empty(_edsm.TransactionCalls);
        Assert.All(dto.Companies, c => Assert.Empty(c.Transactions));
        Assert.Equal(CollectionsCompleteness.Complete, dto.Completeness);
    }

    [Fact]
    public async Task PaymentTabSummary_StillLoadsAllThreeListsPerCompany()
    {
        SeedCustomer(Phone);
        _pact.Answer(Phone, Found(Owned, Rented));

        var dto = (await Service().GetAsync(_agent, PactKey)).Value!;   // the Payment tab's defaults

        Assert.Equal(6, _edsm.TransactionCalls.Count);
        Assert.All(dto.Companies, c => Assert.Equal(["Paid", "Due", "Outstanding"], c.Transactions.Select(l => l.TransactionType)));
        Assert.Equal(CollectionsCompleteness.Complete, dto.Completeness);
    }

    [Fact]
    public async Task ATransactionListCutOffByTheDeadline_IsMarked_AndTheSummaryFiguresAreKept()
    {
        SeedCustomer(Phone);
        _pact.Answer(Phone, Found(Owned));
        _edsm.HangTransactions(EdsmTransactionType.Due);

        var dto = (await Service().GetAsync(_agent, PactKey, includeTransactions: true, deadline: Short)).Value!;

        var owned = Assert.Single(dto.Companies);
        Assert.Equal("Available", owned.Status);
        Assert.NotEmpty(owned.Fields);
        Assert.Equal("Available", owned.Transactions.Single(l => l.TransactionType == "Paid").Status);
        Assert.Equal(CollectionsCompleteness.DeadlineExceeded, owned.Transactions.Single(l => l.TransactionType == "Due").Status);
        Assert.Equal(CollectionsCompleteness.DeadlineExceeded, owned.Transactions.Single(l => l.TransactionType == "Outstanding").Status);
        Assert.Equal(CollectionsCompleteness.Partial, dto.Completeness);
    }

    // ---- 4. Caller cancellation (client disconnect) ----

    [Fact]
    public async Task CallerCancellation_DuringEdsm_Propagates_AndReachesTheGateway()
    {
        SeedCustomer(Phone);
        _pact.Answer(Phone, Found(Owned));
        _edsm.HangSummaryFor(4);
        using var disconnect = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service().GetAsync(_agent, PactKey, includeTransactions: false, deadline: Long, cancellationToken: disconnect.Token));

        Assert.True(_edsm.SawCancellation);
    }

    [Fact]
    public async Task CallerCancellation_DuringDiscovery_Propagates_AndCachesNothing()
    {
        SeedCustomer(Phone);
        _pact.Hang(Phone);
        using var disconnect = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service().GetAsync(_agent, PactKey, includeTransactions: false, deadline: Long, cancellationToken: disconnect.Token));

        Assert.True(_pact.SawCancellation);
        _pact.Answer(Phone, Found(Owned));
        await Service().GetAsync(_agent, PactKey, includeTransactions: false, deadline: Long);
        Assert.Equal(2, _pact.Calls);
    }

    // ---- payment-transactions ----

    [Fact]
    public async Task Transactions_PastTheDeadline_Is503_WithNoRows()
    {
        SeedCustomer(Phone);
        _pact.Answer(Phone, Found(Owned));
        _edsm.HangTransactions(EdsmTransactionType.Paid);

        var result = await Service().GetTransactionsAsync(_agent, PactKey, 4, "Paid", deadline: Short);

        Assert.Equal(CollectionsOutcome.FinanceUnavailable, result.Outcome);
        Assert.Contains("deadline passed", result.Detail);
        Assert.True(_edsm.SawCancellation);
    }

    [Fact]
    public async Task Transactions_CallerCancellation_Propagates()
    {
        SeedCustomer(Phone);
        _pact.Answer(Phone, Found(Owned));
        _edsm.HangTransactions(EdsmTransactionType.Paid);
        using var disconnect = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service().GetTransactionsAsync(_agent, PactKey, 4, "Paid", deadline: Long, cancellationToken: disconnect.Token));
    }

    [Fact]
    public async Task Transactions_ForACompanyMissingFromAPartialDiscovery_Is503_Not404()
    {
        SeedCustomer(Phone, OtherPhone);
        _pact.Answer(Phone, Found(Owned));
        _pact.Answer(OtherPhone, PactCustomerLookupResult.Unavailable("PACT request timed out."));

        var result = await Service().GetTransactionsAsync(_agent, PactKey, 25, "Paid", deadline: Long);

        Assert.Equal(CollectionsOutcome.FinanceUnavailable, result.Outcome);
        Assert.Contains("Company 25 could not be confirmed", result.Detail);
    }

    [Theory]
    [InlineData("Paid")]
    [InlineData("Due")]
    [InlineData("Outstanding")]
    public async Task Transactions_ForTheSelectedCompanyAndType_ReturnRowsWithAmountAndDate(string type)
    {
        SeedCustomer(Phone);
        _pact.Answer(Phone, Found(Owned, Rented));

        var result = await Service().GetTransactionsAsync(_agent, PactKey, 25, type, deadline: Long);

        Assert.Equal(CollectionsOutcome.Success, result.Outcome);
        var dto = result.Value!;
        Assert.Equal(25, dto.CompanyId);
        Assert.Equal(type, dto.TransactionType);
        Assert.Equal((25, type), (_edsm.TransactionCalls.Single().CompanyId, _edsm.TransactionCalls.Single().Type.ToString()));
        Assert.NotEmpty(dto.Items);
        Assert.All(dto.Items, i => Assert.True(i.Amount is not null || i.FormattedRaw is not null));
        Assert.Contains(dto.Items, i => i.Date is not null);
    }

    // ---- the deadline value ----

    [Theory]
    [InlineData(CollectionsReadSurface.Genesys, null, 22)]
    [InlineData(CollectionsReadSurface.Web, null, 60)]
    [InlineData(CollectionsReadSurface.Genesys, 24, 22)]    // TigerGroupWeb for a 30 s flow: capped at 22
    [InlineData(CollectionsReadSurface.Genesys, 15, 15)]    // ... for a 20 s flow
    [InlineData(CollectionsReadSurface.Genesys, 14, 14)]    // ... for a 19 s flow
    [InlineData(CollectionsReadSurface.Genesys, 8, 8)]      // a caller may shorten it
    [InlineData(CollectionsReadSurface.Genesys, 90, 22)]    // never lengthen it
    [InlineData(CollectionsReadSurface.Web, 0, 60)]         // non-positive is ignored
    public void ReadDeadline_IsTheSurfaceDefault_ShortenedButNeverLengthenedByTheCaller(
        CollectionsReadSurface surface, int? requested, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), new CollectionsEdsmOptions().ReadDeadline(surface, requested));
    }

    [Fact]
    public void ReadDeadline_ConfiguredValues_AreClamped()
    {
        var options = new CollectionsEdsmOptions { GenesysReadDeadlineSeconds = 0, WebReadDeadlineSeconds = 100_000 };

        Assert.Equal(TimeSpan.FromSeconds(1), options.ReadDeadline(CollectionsReadSurface.Genesys));
        Assert.Equal(TimeSpan.FromSeconds(CollectionsEdsmOptions.MaxReadDeadlineSeconds), options.ReadDeadline(CollectionsReadSurface.Web));
    }

    // ---- fakes ----

    private static async Task<T> HangAsync<T>(CancellationToken cancellationToken, Action onCancelled)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            onCancelled();
            throw;
        }

        throw new InvalidOperationException("unreachable");
    }

    private sealed class Profiles : ICollectionsCustomerProfiles
    {
        public CustomerDirectoryProfileDto? Profile { get; set; }

        public Task<CustomerDirectoryProfileResult> GetProfileAsync(
            Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, string customerKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(Profile is { } p && p.CustomerKey == customerKey
                ? CustomerDirectoryProfileResult.Success(p)
                : CustomerDirectoryProfileResult.Failure(CustomerDirectoryProfileOutcome.NotFound));
    }

    private sealed class ScriptedPact : IPactCustomerLookupGateway
    {
        private readonly Dictionary<string, PactCustomerLookupResult?> _answers = [];
        private int _calls;

        public int Calls => _calls;

        public bool SawCancellation { get; private set; }

        public void Answer(string phone, PactCustomerLookupResult result) => _answers[phone] = result;

        public void Hang(string phone) => _answers[phone] = null;

        public Task<PactCustomerLookupResult> SearchByMobileAsync(string mobileNumber, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return _answers.TryGetValue(mobileNumber, out var answer) && answer is not null
                ? Task.FromResult(answer)
                : _answers.ContainsKey(mobileNumber)
                    ? HangAsync<PactCustomerLookupResult>(cancellationToken, () => SawCancellation = true)
                    : Task.FromResult(PactCustomerLookupResult.NotFound());
        }
    }

    private sealed class ScriptedEdsm(FixtureEdsmCollectionsGateway inner) : IEdsmCollectionsGateway
    {
        private readonly HashSet<int> _hangSummary = [];
        private readonly Dictionary<int, EdsmResult<EdsmPaymentSummary>> _failSummary = [];
        private readonly HashSet<EdsmTransactionType> _hangTransactions = [];

        public List<int> SummaryCalls { get; } = [];

        public List<(int CompanyId, EdsmTransactionType Type)> TransactionCalls { get; } = [];

        public bool SawCancellation { get; private set; }

        public string SourceName => "Fixture";

        public void HangSummaryFor(int companyId) => _hangSummary.Add(companyId);

        public void FailSummaryFor(int companyId, EdsmOutcome outcome, string message) =>
            _failSummary[companyId] = EdsmResult<EdsmPaymentSummary>.Fail(outcome, message);

        public void HangTransactions(EdsmTransactionType type) => _hangTransactions.Add(type);

        public Task<EdsmResult<EdsmPaymentSummary>> GetPaymentSummaryAsync(int companyId, string tenantId, CancellationToken cancellationToken = default)
        {
            SummaryCalls.Add(companyId);
            if (_hangSummary.Contains(companyId))
            {
                return HangAsync<EdsmResult<EdsmPaymentSummary>>(cancellationToken, () => SawCancellation = true);
            }

            return _failSummary.TryGetValue(companyId, out var failure)
                ? Task.FromResult(failure)
                : inner.GetPaymentSummaryAsync(companyId, tenantId, cancellationToken);
        }

        public Task<EdsmResult<EdsmPaymentTransactions>> GetPaymentTransactionsAsync(
            int companyId, string tenantId, string mobile, EdsmTransactionType type, CancellationToken cancellationToken = default)
        {
            TransactionCalls.Add((companyId, type));
            return _hangTransactions.Contains(type)
                ? HangAsync<EdsmResult<EdsmPaymentTransactions>>(cancellationToken, () => SawCancellation = true)
                : inner.GetPaymentTransactionsAsync(companyId, tenantId, mobile, type, cancellationToken);
        }

        public Task<EdsmResult<IReadOnlyList<EdsmDueInstallment>>> GetDueInstallmentsAsync(
            int companyId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default) =>
            inner.GetDueInstallmentsAsync(companyId, fromDate, toDate, cancellationToken);
    }
}

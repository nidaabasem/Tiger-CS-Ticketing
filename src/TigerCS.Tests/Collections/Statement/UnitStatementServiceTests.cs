using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Integrations.Modules.CollectionsIntegration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Statement;

/// <summary>
/// The WHOLE statement service (contract discovery -> account resolution -> EDSM reads -> composition) with a fake PACT contract list and the fixture EDSM gateway.
/// It proves that one unit's statement never reads or shows another account's amounts. It does NOT prove how production PACT assigns TenantId to units - that is the open external question.
/// </summary>
public sealed class UnitStatementServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);
    private const string Phone = "+971500000002";

    private sealed class RecordingEdsm(IEdsmCollectionsGateway inner) : IEdsmCollectionsGateway
    {
        public List<(int, string)> Summaries { get; } = [];
        public string SourceName => "Test";
        public Task<EdsmResult<EdsmPaymentSummary>> GetPaymentSummaryAsync(int companyId, string tenantId, CancellationToken cancellationToken = default) { Summaries.Add((companyId, tenantId)); return inner.GetPaymentSummaryAsync(companyId, tenantId, cancellationToken); }
        public Task<EdsmResult<EdsmPaymentTransactions>> GetPaymentTransactionsAsync(int companyId, string tenantId, string mobile, EdsmTransactionType type, CancellationToken cancellationToken = default) => inner.GetPaymentTransactionsAsync(companyId, tenantId, mobile, type, cancellationToken);
        public Task<EdsmResult<IReadOnlyList<EdsmDueInstallment>>> GetDueInstallmentsAsync(int companyId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default) => inner.GetDueInstallmentsAsync(companyId, fromDate, toDate, cancellationToken);
    }

    private sealed class NoProfiles : ICollectionsCustomerProfiles
    {
        public Task<CustomerDirectoryProfileResult> GetProfileAsync(Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, string customerKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(CustomerDirectoryProfileResult.Failure(CustomerDirectoryProfileOutcome.NotFound));
    }

    private sealed class Rig
    {
        public FakePactCustomerLookupGateway Pact { get; } = new();
        public RecordingEdsm Edsm { get; }
        public CollectionsUnitStatementService Service { get; }

        public Rig()
        {
            PactContractDiscovery.ClearCache();
            var options = new CollectionsOptions { Enabled = true };
            var edsmOptions = new CollectionsEdsmOptions { EdsmNumberCulture = "en-US" };
            var time = new FakeTimeProvider(Now);
            Edsm = new RecordingEdsm(new FixtureEdsmCollectionsGateway(Options.Create(edsmOptions)));
            var account = new CollectionsPaymentSummaryAppService(options, edsmOptions, new CollectionsAuthorizationService(options, new FakeDepartmentRepository()), new CollectionsClock(options, time),
                new NoProfiles(), Pact, Edsm, new PactAccountMappingCache(time));
            Service = new CollectionsUnitStatementService(edsmOptions, new CollectionsClock(options, time), new PactContractDiscovery(Pact, time), account, Edsm, NullLogger<CollectionsUnitStatementService>.Instance);
        }

        public void Tenant(string tenant, params string[] unitCodes) =>
            Pact.Seed(Phone, new PactCustomerMatchDto(tenant, "Customer", Phone, null, "2",
                unitCodes.Select(c => new PactContractDto("U-" + c, "C-" + c, c[(c.IndexOf('-') + 1)..], "Tower", null, 4, null, c)).ToList()));

        public Task<TigerCS.Application.Modules.Collections.Dto.UnitPaymentStatementDto> Get(string key, string? tenant = null) => Service.GetAsync(new UnitStatementRequest(4, key, tenant, [Phone]), default);
    }

    [Fact]
    public async Task ACustomerWithTwoUnitsOnSeparateAccounts_GetsEachUnitsOwnStatement_AndNeverTheOthers()
    {
        var r = new Rig();
        r.Tenant("3001", "TP140-909");          // the fixture has real figures for company 4 / tenant 3001
        r.Tenant("4444", "TP140-101");          // a second account: the fixture has no figures for it (zeros)

        var first = await r.Get("TP140-909");
        Assert.True(first.IsAvailable);
        Assert.Equal(1250000m, first.Total!.Value);
        Assert.Equal(812500m, first.Paid!.Value);
        Assert.Equal("3001", first.TenantId);
        Assert.Equal([(4, "3001")], r.Edsm.Summaries);                       // tenant 4444 was never asked for unit 909

        r.Edsm.Summaries.Clear();
        var second = await r.Get("TP140-101");
        Assert.Equal("4444", second.TenantId);
        Assert.NotEqual(1250000m, second.Total?.Value);                       // not unit 909's money
        Assert.Equal([(4, "4444")], r.Edsm.Summaries);
    }

    [Fact]
    public async Task TwoUnitsOnOneAccount_AreNeverShownAsOne_NothingIsReadFromEdsm()
    {
        var r = new Rig();
        r.Tenant("3001", "TP140-909", "TP140-407");

        var s = await r.Get("TP140-909");

        Assert.Equal("NotExclusive", s.Status);
        Assert.Contains("TP140-407", s.OtherUnits);
        Assert.Null(s.Total); Assert.Null(s.Paid); Assert.Empty(s.Rows);
        Assert.Empty(r.Edsm.Summaries);
    }

    [Theory]
    [InlineData("TP140-909*")]
    [InlineData("TP140-9**09")]
    public async Task ACancelledUnitCode_IsRefused_EvenWhenTheActiveUnitExists_AndNothingIsRead(string code)
    {
        var r = new Rig();
        r.Tenant("3001", "TP140-909");
        var s = await r.Get(code);
        Assert.Equal("UnitNotResolved", s.Status);
        Assert.Empty(r.Edsm.Summaries);
    }

    [Fact]
    public async Task WhenPactCannotBeReached_NothingIsShown_AndNoAmountIsGuessed()
    {
        var r = new Rig();
        r.Pact.ForcedOutcome = PactCustomerLookupOutcome.Unavailable;
        var s = await r.Get("TP140-909");
        Assert.Contains(s.Status, new[] { "UnitNotFound", "Unverified", "SourceUnavailable" });
        Assert.False(s.IsAvailable);
        Assert.Empty(r.Edsm.Summaries);
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Domain.Modules.Collections;
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
        public List<string> Mobiles { get; } = [];
        public string SourceName => "Test";
        public Task<EdsmResult<EdsmPaymentSummary>> GetPaymentSummaryAsync(int companyId, string tenantId, CancellationToken cancellationToken = default) { Summaries.Add((companyId, tenantId)); return inner.GetPaymentSummaryAsync(companyId, tenantId, cancellationToken); }
        public Task<EdsmResult<EdsmPaymentTransactions>> GetPaymentTransactionsAsync(int companyId, string tenantId, string mobile, EdsmTransactionType type, CancellationToken cancellationToken = default) { Mobiles.Add(mobile); return inner.GetPaymentTransactionsAsync(companyId, tenantId, mobile, type, cancellationToken); }
        public Task<EdsmResult<IReadOnlyList<EdsmDueInstallment>>> GetDueInstallmentsAsync(int companyId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default) => inner.GetDueInstallmentsAsync(companyId, fromDate, toDate, cancellationToken);
    }

    /// <summary>PACT's unit/account directory: units with an open balance AND fully paid ones (OpenRows = 0), plus whether the source is accepted as complete.</summary>
    private sealed class FakeUnits : IPactReceivablesSource, IUnitReceivablesSource
    {
        public List<PactDirectoryUnit> Directory { get; } = [];
        public bool Complete { get; set; }
        public int Reads { get; private set; }
        public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) => Task.FromResult(new PactReceivablesSnapshot([], Now, false));
        public Task<PactUnitReceivables> ReadUnitAsync(string unitKey, int? companyId, DateOnly today, CancellationToken cancellationToken) => throw new InvalidOperationException("The statement must not depend on the open-balance snapshot.");
        public Task<PactAccountDirectory> ReadAccountDirectoryAsync(string unitKey, int? companyId, CancellationToken cancellationToken)
        {
            Reads++;
            var matches = Directory.Where(u => !u.IsCancelled && CollectionsUnitKey.Normalize(u.UnitCode) == unitKey && CollectionsUnitKey.IsListable(u.UnitCode) && (companyId is null || u.CompanyId == companyId)).ToList();
            var account = Directory.Where(u => matches.Any(m => m.CompanyId == u.CompanyId && m.TenantId == u.TenantId)).ToList();
            return Task.FromResult(new PactAccountDirectory(matches, account, Complete, "Test directory", Now));
        }
    }

    private sealed class NoProfiles : ICollectionsCustomerProfiles
    {
        public Task<CustomerDirectoryProfileResult> GetProfileAsync(Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, string customerKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(CustomerDirectoryProfileResult.Failure(CustomerDirectoryProfileOutcome.NotFound));
    }

    private sealed class Rig
    {
        public FakePactCustomerLookupGateway Pact { get; } = new();
        public FakeUnits Units { get; } = new();
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
            Service = new CollectionsUnitStatementService(edsmOptions, new CollectionsClock(options, time), new PactContractDiscovery(Pact, time), account, Edsm, Units, NullLogger<CollectionsUnitStatementService>.Instance);
        }

        /// <summary>A unit in the PACT directory. <paramref name="open"/> = rows still open (0 = fully paid). PACT holds no mobile for any of them.</summary>
        public void Unit(string tenant, string code, int unitId, int company = 4, int open = 3) =>
            Units.Directory.Add(new PactDirectoryUnit(company, tenant, unitId, code, open, open == 0 ? 3 : 1, code.Contains('*')));

        /// <summary>PACT's contract list for the customer's phone (the OPTIONAL extra proof).</summary>
        public void Contracts(string tenant, params string[] unitCodes) =>
            Pact.Seed(Phone, new PactCustomerMatchDto(tenant, "Customer", Phone, null, "2",
                unitCodes.Select(c => new PactContractDto("U-" + c, "C-" + c, c[(c.IndexOf('-') + 1)..], "Tower", null, 4, null, c)).ToList()));

        public Task<UnitPaymentStatementDto> Get(string key, string? tenant = null, params string[] phones) => Service.GetAsync(new UnitStatementRequest(4, key, tenant, phones), default);
    }

    // ---- identity from the directory: a fully paid unit with no PACT mobile ----

    [Fact]
    public async Task AFullyPaidUnit_WithNoPactMobile_IsResolvedFromTheDirectory_AndGetsItsStatement()
    {
        var r = new Rig();
        r.Units.Complete = true;                                       // the directory source is accepted as complete scope evidence
        r.Unit("3001", "TP140-909", 41230, open: 0);                   // nothing is open on the unit: the open-balance snapshot knows nothing about it

        var s = await r.Get("TP140-909");                              // and no phone is known

        Assert.True(s.IsAvailable, s.Detail);
        Assert.Equal((4, "3001"), (s.CompanyId, s.TenantId));
        Assert.Equal((1250000m, 812500m), (s.Total!.Value, s.Paid!.Value));
        Assert.Equal([(4, "3001")], r.Edsm.Summaries);
        Assert.All(r.Edsm.Mobiles, m => Assert.Equal(CollectionsUnitStatementService.MobileNotUsed, m));   // "0": UNVERIFIED against real EDSM
        Assert.Equal(0, r.Pact.SearchCallCount);                       // no mandatory phone lookup
        Assert.Contains(s.Notes, n => n.Contains("unit/account directory"));
    }

    [Fact]
    public async Task AnAccountWithAFullyPaidSibling_NeverShowsTheAccountTotalAsOneUnitsAmounts()
    {
        var r = new Rig();
        r.Units.Complete = true;
        r.Unit("3001", "TP140-909", 41230, open: 3);
        r.Unit("3001", "TP140-101", 41231, open: 0);                   // fully paid, nothing open - but its payments are inside EDSM's Paid and Total

        var s = await r.Get("TP140-909");

        Assert.Equal("NotExclusive", s.Status);
        Assert.Contains("TP140-101 (fully paid)", s.OtherUnits);
        Assert.Contains("fully paid units all feed", s.Detail);
        Assert.Null(s.Total); Assert.Null(s.Paid); Assert.Null(s.Due); Assert.Null(s.Outstanding); Assert.Empty(s.Rows);
        Assert.Empty(r.Edsm.Summaries);
        var sibling = await r.Get("TP140-101");                        // the paid sibling is refused for the same reason
        Assert.Equal("NotExclusive", sibling.Status);
        Assert.Empty(r.Edsm.Summaries);
    }

    [Fact]
    public async Task ACancelledSiblingOfTheAccount_AlsoWithholds_ItsAmountsCannotBeSeparated()
    {
        var r = new Rig();
        r.Units.Complete = true;
        r.Unit("3001", "TP140-909", 41230);
        r.Unit("3001", "TP140-407*", 41232);
        var s = await r.Get("TP140-909");
        Assert.Equal("NotExclusive", s.Status);
        Assert.Contains(s.OtherUnits, o => o.Contains("TP140-407*") && o.Contains("cancelled"));
        Assert.Empty(r.Edsm.Summaries);
    }

    // ---- scope evidence: an account total is never a unit's statement without COMPLETE evidence ----

    [Fact]
    public async Task OpenBalanceDataAlone_WithAnIncompleteDirectory_ProvesNothing_TheExactMissingQueryIsNamed()
    {
        var r = new Rig();                                             // directory NOT accepted as complete (the default)
        r.Unit("3001", "TP140-909", 41230);

        var s = await r.Get("TP140-909");

        Assert.Equal("ScopeUnproven", s.Status);
        Assert.Contains("What is missing", s.Detail);
        Assert.Contains("EVERY unit / contract of a (CompanyId, TenantId) account", s.Detail);
        Assert.Null(s.Total); Assert.Null(s.Paid); Assert.Empty(s.Rows);
        Assert.Empty(r.Edsm.Summaries);                                // the account was not even asked
    }

    [Fact]
    public async Task AnIncompleteDirectory_AndAFailingContractList_StillShowNoAmount()
    {
        var r = new Rig();
        r.Unit("3001", "TP140-909", 41230);
        r.Pact.ForcedOutcome = PactCustomerLookupOutcome.Unavailable;
        var s = await r.Get("TP140-909", "3001", Phone);
        Assert.Equal("ScopeUnproven", s.Status);
        Assert.Empty(r.Edsm.Summaries);
    }

    [Fact]
    public async Task AnIncompleteDirectory_IsEnough_WhenPactsContractListConfirmsTheAccountHoldsOnlyThisUnit()
    {
        var r = new Rig();
        r.Unit("3001", "TP140-909", 41230);
        r.Contracts("3001", "TP140-909");                              // complete answer for every phone: the account's only contract
        var s = await r.Get("TP140-909", "3001", Phone);
        Assert.True(s.IsAvailable, s.Detail);
        Assert.DoesNotContain(s.Notes, n => n.Contains("unit/account directory"));
    }

    [Fact]
    public async Task ACompleteDirectory_IsOverruledByTheContractList_WhenItShowsAnotherContractOfTheTenant()
    {
        var r = new Rig();
        r.Units.Complete = true;
        r.Unit("3001", "TP140-909", 41230);
        r.Contracts("3001", "TP140-909", "TP115-PK1");                 // a parking contract the directory does not know
        var s = await r.Get("TP140-909", "3001", Phone);
        Assert.Equal("NotExclusive", s.Status);
        Assert.Contains("TP115-PK1", s.OtherUnits);
        Assert.Empty(r.Edsm.Summaries);
    }

    [Fact]
    public async Task ACustomerWithTwoUnitsOnSeparateAccounts_GetsEachUnitsOwnStatement_AndNeverTheOthers()
    {
        var r = new Rig();
        r.Units.Complete = true;
        r.Unit("3001", "TP140-909", 41230);     // the fixture has real figures for company 4 / tenant 3001
        r.Unit("4444", "TP140-101", 41231, open: 0);     // a second account, fully paid: the fixture has no figures for it (zeros)

        var first = await r.Get("TP140-909");
        Assert.True(first.IsAvailable);
        Assert.Equal(1250000m, first.Total!.Value);
        Assert.Equal([(4, "3001")], r.Edsm.Summaries);                       // tenant 4444 was never asked for unit 909

        r.Edsm.Summaries.Clear();
        var second = await r.Get("TP140-101");
        Assert.Equal("4444", second.TenantId);
        Assert.NotEqual(1250000m, second.Total?.Value);                       // not unit 909's money
        Assert.Equal([(4, "4444")], r.Edsm.Summaries);
    }

    [Fact]
    public async Task APactOrCrmMobile_IsPassedAlong_WhenOneIsKnown_ButIsNeverRequired()
    {
        var r = new Rig();
        r.Units.Complete = true;
        r.Unit("3001", "TP140-909", 41230);
        var s = await r.Service.GetAsync(new UnitStatementRequest(4, "TP140-909", "3001", []) { FallbackMobile = "+971509999999" }, default);
        Assert.True(s.IsAvailable);
        Assert.All(r.Edsm.Mobiles, m => Assert.Equal("971509999999", m));
    }

    [Fact]
    public async Task ATenantThatDiffersFromTheDirectorysTenant_IsAnIdentityMismatch()
    {
        var r = new Rig();
        r.Units.Complete = true;
        r.Unit("3001", "TP140-909", 41230);
        Assert.Equal("IdentityMismatch", (await r.Get("TP140-909", "9999")).Status);
        Assert.Empty(r.Edsm.Summaries);
    }

    [Theory]
    [InlineData("TP140-909*")]
    [InlineData("TP140-9**09")]
    [InlineData("**TP140-909")]
    public async Task ACancelledUnitCode_IsRefused_EvenWhenTheActiveUnitExists_AndNothingIsReadOrMatched(string code)
    {
        var r = new Rig();
        r.Units.Complete = true;
        r.Unit("3001", "TP140-909", 41230);
        r.Unit("3002", code, 41299);             // even a starred entry in the data is never used
        var s = await r.Get(code);
        Assert.Equal("UnitNotResolved", s.Status);
        Assert.Empty(r.Edsm.Summaries);
        Assert.Equal(0, r.Units.Reads);
    }

    [Fact]
    public async Task AUnitUnknownToPact_WithNoPhone_SaysSo_NamesTheMissingSource_AndShowsNoAmount()
    {
        var r = new Rig();
        r.Units.Complete = true;
        var s = await r.Get("TP140-909");
        Assert.Equal("UnitNotFound", s.Status);
        Assert.Contains("What is missing", s.Detail);
        Assert.Empty(r.Edsm.Summaries);
    }

    [Fact]
    public async Task AUnitAbsentFromTheDirectory_FallsBackToTheContractList_OnlyWhenAPhoneIsKnown()
    {
        var r = new Rig();
        r.Contracts("3001", "TP140-909");
        var s = await r.Get("TP140-909", null, Phone);
        Assert.True(s.IsAvailable, s.Detail);
        Assert.Equal("3001", s.TenantId);
    }
}

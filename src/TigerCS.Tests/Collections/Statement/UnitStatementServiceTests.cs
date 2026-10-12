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

    /// <summary>The existing PACT unit data: one identity per unit with an open balance (company, tenant, unit id, unit code), and the tenant's other units derived from the same list.</summary>
    private sealed class FakeUnits : IPactReceivablesSource, IUnitReceivablesSource
    {
        public List<PactUnitIdentity> Identities { get; } = [];
        public int Reads { get; private set; }
        public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) => Task.FromResult(new PactReceivablesSnapshot([], Now, false));
        public Task<PactUnitReceivables> ReadUnitAsync(string unitKey, int? companyId, DateOnly today, CancellationToken cancellationToken)
        {
            Reads++;
            var ids = Identities.Where(i => CollectionsUnitKey.Normalize(i.UnitCode) == unitKey && CollectionsUnitKey.IsListable(i.UnitCode) && (companyId is null || i.CompanyId == companyId)).ToList();
            var tenantUnits = Identities.Where(o => CollectionsUnitKey.IsListable(o.UnitCode) && ids.Any(i => i.CompanyId == o.CompanyId && i.TenantId == o.TenantId)).Select(o => new PactTenantUnit(o.CompanyId, o.TenantId, o.UnitId, o.UnitCode)).ToList();
            var snapshot = new SnapshotStatusDto(new[] { 4, 32 }.Select(c => new SnapshotCompanyStatusDto(c, "Co" + c, true, Now, Now, "Succeeded", null, 0, 10, null, null, 0, 0, 0, 0, 0, 0, "Fresh", 1)).ToList(), [], 120);
            return Task.FromResult(new PactUnitReceivables(snapshot, ids, [], new Dictionary<int, CrmUnitLink>(), true, Now, tenantUnits));
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

        /// <summary>A unit with an open balance in the PACT unit data. The PACT mobile is blank unless given.</summary>
        public void Unit(string tenant, string code, int unitId, int company = 4, string mobile = "") =>
            Units.Identities.Add(new PactUnitIdentity(company, tenant, unitId, code, "Pact Holder", mobile, "", "140", "Tower", 3, 3));

        /// <summary>PACT's contract list for the customer's phone (the OPTIONAL extra proof).</summary>
        public void Contracts(string tenant, params string[] unitCodes) =>
            Pact.Seed(Phone, new PactCustomerMatchDto(tenant, "Customer", Phone, null, "2",
                unitCodes.Select(c => new PactContractDto("U-" + c, "C-" + c, c[(c.IndexOf('-') + 1)..], "Tower", null, 4, null, c)).ToList()));

        public Task<UnitPaymentStatementDto> Get(string key, string? tenant = null, params string[] phones) => Service.GetAsync(new UnitStatementRequest(4, key, tenant, phones), default);
    }

    [Fact]
    public async Task AUnitWithNoPactMobileAndNoKnownPhone_StillGetsItsStatement_FromItsCompanyTenantAndUnitIdentifiers()
    {
        var r = new Rig();
        r.Unit("3001", "TP140-909", 41230, mobile: "");                // PACT holds no mobile for the unit

        var s = await r.Get("TP140-909");                              // and the caller knows no phone either

        Assert.True(s.IsAvailable);
        Assert.Equal((4, "3001"), (s.CompanyId, s.TenantId));
        Assert.Equal((1250000m, 812500m, 62500m, 375000m), (s.Total!.Value, s.Paid!.Value, s.Due!.Value, s.Outstanding!.Value));
        Assert.Equal(5, s.Rows.Count);                                 // the Paid, Due and Outstanding payment rows of the account
        Assert.Equal([(4, "3001")], r.Edsm.Summaries);
        Assert.All(r.Edsm.Mobiles, m => Assert.Equal(CollectionsUnitStatementService.MobileNotUsed, m));
        Assert.Equal(0, r.Pact.SearchCallCount);                  // PACT's contract list was never asked: it is not a gate
        Assert.Contains(s.Notes, n => n.Contains("receivables snapshot"));   // the snapshot-only proof is stated
    }

    [Fact]
    public async Task APactOrCrmMobile_IsPassedAlong_WhenOneIsKnown_ButIsNeverRequired()
    {
        var r = new Rig();
        r.Unit("3001", "TP140-909", 41230, mobile: "+971 50 000 0002");
        var s = await r.Service.GetAsync(new UnitStatementRequest(4, "TP140-909", "3001", []) { FallbackMobile = "+971509999999" }, default);
        Assert.True(s.IsAvailable);
        Assert.All(r.Edsm.Mobiles, m => Assert.Equal("971500000002", m));
    }

    [Fact]
    public async Task ACustomerWithTwoUnitsOnSeparateAccounts_GetsEachUnitsOwnStatement_AndNeverTheOthers()
    {
        var r = new Rig();
        r.Unit("3001", "TP140-909", 41230);     // the fixture has real figures for company 4 / tenant 3001
        r.Unit("4444", "TP140-101", 41231);     // a second account: the fixture has no figures for it (zeros)

        var first = await r.Get("TP140-909");
        Assert.True(first.IsAvailable);
        Assert.Equal(1250000m, first.Total!.Value);
        Assert.Equal("3001", first.TenantId);
        Assert.Equal([(4, "3001")], r.Edsm.Summaries);                       // tenant 4444 was never asked for unit 909

        r.Edsm.Summaries.Clear();
        var second = await r.Get("TP140-101");
        Assert.Equal("4444", second.TenantId);
        Assert.NotEqual(1250000m, second.Total?.Value);                       // not unit 909's money
        Assert.Equal([(4, "4444")], r.Edsm.Summaries);
    }

    [Fact]
    public async Task TwoUnitsOnOneAccount_AreNeverShownAsOne_TheSourceLimitationIsNamed_AndNothingIsReadFromEdsm()
    {
        var r = new Rig();
        r.Unit("3001", "TP140-909", 41230);
        r.Unit("3001", "TP140-407", 41231);

        var s = await r.Get("TP140-909");

        Assert.Equal("NotExclusive", s.Status);
        Assert.Contains("TP140-407", s.OtherUnits);
        Assert.Contains("no unit parameter", s.Detail);                        // the exact limitation of the source
        Assert.Null(s.Total); Assert.Null(s.Paid); Assert.Empty(s.Rows);
        Assert.Empty(r.Edsm.Summaries);
    }

    [Fact]
    public async Task AFullyPaidSibling_NotInTheSnapshot_IsFoundThroughTheOptionalContractList_AndWithholdsTheStatement()
    {
        var r = new Rig();
        r.Unit("3001", "TP140-909", 41230);
        r.Contracts("3001", "TP140-909", "TP115-PK1");                          // a parking contract with nothing open on the same tenant
        var s = await r.Get("TP140-909", "3001", Phone);
        Assert.Equal("NotExclusive", s.Status);
        Assert.Contains("TP115-PK1", s.OtherUnits);
        Assert.Empty(r.Edsm.Summaries);
    }

    [Fact]
    public async Task WhenTheContractListConfirmsTheAccountHoldsOnlyThisUnit_NoSnapshotCaveatIsAdded()
    {
        var r = new Rig();
        r.Unit("3001", "TP140-909", 41230);
        r.Contracts("3001", "TP140-909");
        var s = await r.Get("TP140-909", "3001", Phone);
        Assert.True(s.IsAvailable);
        Assert.DoesNotContain(s.Notes, n => n.Contains("receivables snapshot"));
    }

    [Fact]
    public async Task AFailingContractList_DoesNotBlockTheStatement_ItOnlyKeepsTheCaveat()
    {
        var r = new Rig();
        r.Unit("3001", "TP140-909", 41230);
        r.Pact.ForcedOutcome = PactCustomerLookupOutcome.Unavailable;
        var s = await r.Get("TP140-909", "3001", Phone);
        Assert.True(s.IsAvailable);
        Assert.Contains(s.Notes, n => n.Contains("receivables snapshot"));
    }

    [Fact]
    public async Task ATenantThatDiffersFromTheSnapshotsTenant_IsAnIdentityMismatch()
    {
        var r = new Rig();
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
        r.Unit("3001", "TP140-909", 41230);
        r.Unit("3002", code, 41299);             // even a starred identity in the data is never used
        var s = await r.Get(code);
        Assert.Equal("UnitNotResolved", s.Status);
        Assert.Empty(r.Edsm.Summaries);
        Assert.Equal(0, r.Units.Reads);
    }

    [Fact]
    public async Task AUnitUnknownToPact_WithNoPhone_SaysSo_AndShowsNoAmount()
    {
        var r = new Rig();
        var s = await r.Get("TP140-909");
        Assert.Equal("UnitNotFound", s.Status);
        Assert.Empty(r.Edsm.Summaries);
    }

    [Fact]
    public async Task AFullyPaidUnit_NotInTheSnapshot_FallsBackToTheContractList_WhenAPhoneIsKnown()
    {
        var r = new Rig();
        r.Contracts("3001", "TP140-909");
        var s = await r.Get("TP140-909", null, Phone);
        Assert.True(s.IsAvailable);
        Assert.Equal("3001", s.TenantId);
    }
}

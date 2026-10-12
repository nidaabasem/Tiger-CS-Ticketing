using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Crm;

/// <summary>
/// A CRM project with no verified mapping row (project 94 / unit 3302 in the reported case) is resolved per unit from the SAME customer's own PACT contracts: the apartment number must
/// match exactly one active, non-cancelled contract. No project id, tower or company is hardcoded; names are never used. Synthetic data: it proves the rule, not what production PACT returns.
/// </summary>
public sealed partial class CustomerUnitLinkServiceTests
{
    private sealed class FakeEvidence : ICollectionsCrmProjectEvidenceStore
    {
        public List<CrmProjectEvidence> Recorded { get; } = [];
        public Task RecordAsync(IReadOnlyList<CrmProjectEvidence> evidence, CancellationToken cancellationToken) { Recorded.AddRange(evidence); return Task.CompletedTask; }
    }

    private static (CustomerUnitLinkService Service, FakeEvidence Evidence) WithEvidence(Rig r)
    {
        PactContractDiscovery.ClearCache();
        var evidence = new FakeEvidence();
        var service = new CustomerUnitLinkService(r.Options, new PactReceivablesOptions { Enabled = true }, new ReceivablesSnapshotOptions(), new CollectionsCrmOwnersOptions { Enabled = true },
            new(r.Options, new FakeDepartmentRepository()), new(r.Options, new FakeTimeProvider(Now)), r.Crm, r.Pact, r.Map, r.Towers, r.Units, NullLogger<CustomerUnitLinkService>.Instance,
            new PactContractDiscovery(r.Pact, new FakeTimeProvider(Now)), null, evidence);
        return (service, evidence);
    }

    private static void PactHolds(Rig r, params PactContractDto[] contracts) =>
        r.Pact.Result = PactCustomerLookupResult.Success([new PactCustomerMatchDto("T-3302", "Ben Israeli", PhoneE164, null, null, contracts)]);

    private static PactContractDto PactContract(string code, int company, DateOnly? ends = null) => new(code, "C-" + code, code[(code.IndexOf('-') + 1)..], "VOLGA Tower", null, company, ends, code);

    private static void BenIsraeli(Rig r) => CrmReturns(r, Buyer(437381, "BEN ISRAELI", PhoneE164, null, CrmUnit(3302, "3302", project: 94, projectName: "VOLGA Tower")));

    [Fact]
    public async Task AProjectWithoutAMappingRow_IsResolvedFromTheCustomersOwnPactContract_AndTheObservationIsRecorded()
    {
        var r = new Rig(); BenIsraeli(r);
        PactHolds(r, PactContract("TP125-3302", 4));
        r.Pact4("125", 3302, "T-3302", overdue: 500m);
        var (service, evidence) = WithEvidence(r);

        var unit = Assert.Single((await service.FindForCrmCustomerAsync(r.Manager, 437381, [Phone], null)).Value!.Candidates);

        Assert.Equal(("Available", "TP125-3302", 4), (unit.FinancialStatus, unit.UnitKey, unit.CompanyId));
        Assert.Equal(500m, unit.Total);
        var seen = Assert.Single(evidence.Recorded);
        Assert.Equal((94, "TP125", 4, 3302), (seen.CrmProjectId, seen.ProjectCode, seen.CompanyId, seen.CrmUnitId));
    }

    [Fact]
    public async Task TheSameApartmentInTwoTowers_IsAmbiguous_NothingIsChosen_NothingIsRecorded()
    {
        var r = new Rig(); BenIsraeli(r);
        PactHolds(r, PactContract("TP125-3302", 4), PactContract("TP127-3302", 32));
        var (service, evidence) = WithEvidence(r);

        var unit = Assert.Single((await service.FindForCrmCustomerAsync(r.Manager, 437381, [Phone], null)).Value!.Candidates);

        Assert.Equal(("MatchFailed", "ProjectMappingAmbiguous"), (unit.FinancialStatus, unit.FinancialReason));
        Assert.Empty(evidence.Recorded);
        Assert.Empty(r.Units.Reads);
    }

    [Fact]
    public async Task WhenNoContractHasTheApartment_TheMappingStaysMissing_WithAReason_AndNoAmounts()
    {
        var r = new Rig(); BenIsraeli(r);
        PactHolds(r, PactContract("TP125-1111", 4));
        var (service, evidence) = WithEvidence(r);

        var unit = Assert.Single((await service.FindForCrmCustomerAsync(r.Manager, 437381, [Phone], null)).Value!.Candidates);

        Assert.Equal(("MatchFailed", "ProjectMappingMissing"), (unit.FinancialStatus, unit.FinancialReason));
        Assert.Contains("3302", unit.FinancialDetail);
        Assert.Null(unit.Total);
        Assert.Empty(evidence.Recorded);
    }

    [Theory]
    [InlineData("TP125-3302*")]
    [InlineData("TP125-33*02")]
    [InlineData("TP125-3302**")]
    public async Task ACancelledPactUnit_WithOneOrMoreAsterisks_IsNeverUsedAsEvidence_AndNeverMatchedWithTheMarkRemoved(string cancelledCode)
    {
        var r = new Rig(); BenIsraeli(r);
        PactHolds(r, PactContract(cancelledCode, 4));
        r.Pact4("125", 3302, "T-3302", overdue: 500m);        // an ACTIVE TP125-3302 exists in the snapshot: it must still not be linked through the starred code
        var (service, evidence) = WithEvidence(r);

        var unit = Assert.Single((await service.FindForCrmCustomerAsync(r.Manager, 437381, [Phone], null)).Value!.Candidates);

        Assert.Equal("ProjectMappingMissing", unit.FinancialReason);
        Assert.Empty(evidence.Recorded);
        Assert.Empty(r.Units.Reads);
    }

    [Fact]
    public async Task ACrmUnitWhoseOwnCodeIsStarred_IsNotACandidateAtAll()
    {
        var r = new Rig();
        r.Map.Rows.Add(new(94, "TP125", 4, "Manual"));
        CrmReturns(r, Buyer(437381, "BEN ISRAELI", PhoneE164, null, CrmUnit(3302, "3302*", project: 94, projectName: "VOLGA Tower"), CrmUnit(3303, "33*03", project: 94, projectName: "VOLGA Tower")));
        r.Pact4("125", 3302, "T-3302", overdue: 500m);
        var result = (await r.Service.FindForCrmCustomerAsync(r.Manager, 437381, [Phone], null)).Value!;
        Assert.Empty(result.Candidates);
        Assert.Empty(r.Units.Reads);
    }

    [Fact]
    public async Task AnEndedContract_IsNotEvidence()
    {
        var r = new Rig(); BenIsraeli(r);
        PactHolds(r, PactContract("TP125-3302", 4, ends: new DateOnly(2026, 1, 1)));
        var (service, evidence) = WithEvidence(r);
        var unit = Assert.Single((await service.FindForCrmCustomerAsync(r.Manager, 437381, [Phone], null)).Value!.Candidates);
        Assert.Equal("ProjectMappingMissing", unit.FinancialReason);
        Assert.Empty(evidence.Recorded);
    }

    [Fact]
    public async Task AVerifiedMappingRow_AlwaysWins_PactContractsAreNotAsked()
    {
        var r = new Rig(); BenIsraeli(r);
        r.Map.Rows.Add(new(94, "TP125", 4, "Manual"));
        r.Pact4("125", 3302, "T-3302", overdue: 500m);
        var (service, evidence) = WithEvidence(r);
        var unit = Assert.Single((await service.FindForCrmCustomerAsync(r.Manager, 437381, [Phone], null)).Value!.Candidates);
        Assert.Equal("Available", unit.FinancialStatus);
        Assert.Empty(r.Pact.Calls);
        Assert.Empty(evidence.Recorded);
    }

    // ---- the whole flow: CRM unit -> exact unit code in the PACT unit data -> company / tenant / unit -> statement; no PACT mobile, no contract lookup ----

    private sealed class RecordingEdsm(TigerCS.Application.Modules.Collections.Abstractions.IEdsmCollectionsGateway inner) : TigerCS.Application.Modules.Collections.Abstractions.IEdsmCollectionsGateway
    {
        public List<(int, string)> Summaries { get; } = [];
        public string SourceName => "Test";
        public Task<TigerCS.Application.Modules.Collections.Abstractions.EdsmResult<TigerCS.Application.Modules.Collections.Abstractions.EdsmPaymentSummary>> GetPaymentSummaryAsync(int companyId, string tenantId, CancellationToken cancellationToken = default) { Summaries.Add((companyId, tenantId)); return inner.GetPaymentSummaryAsync(companyId, tenantId, cancellationToken); }
        public Task<TigerCS.Application.Modules.Collections.Abstractions.EdsmResult<TigerCS.Application.Modules.Collections.Abstractions.EdsmPaymentTransactions>> GetPaymentTransactionsAsync(int companyId, string tenantId, string mobile, TigerCS.Application.Modules.Collections.Abstractions.EdsmTransactionType type, CancellationToken cancellationToken = default) => inner.GetPaymentTransactionsAsync(companyId, tenantId, mobile, type, cancellationToken);
        public Task<TigerCS.Application.Modules.Collections.Abstractions.EdsmResult<IReadOnlyList<TigerCS.Application.Modules.Collections.Abstractions.EdsmDueInstallment>>> GetDueInstallmentsAsync(int companyId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default) => inner.GetDueInstallmentsAsync(companyId, fromDate, toDate, cancellationToken);
    }

    private sealed class NoProfiles : TigerCS.Application.Modules.Collections.Services.ICollectionsCustomerProfiles
    {
        public Task<TigerCS.Application.Modules.Ticketing.Dto.CustomerDirectoryProfileResult> GetProfileAsync(Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, string customerKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(TigerCS.Application.Modules.Ticketing.Dto.CustomerDirectoryProfileResult.Failure(TigerCS.Application.Modules.Ticketing.Dto.CustomerDirectoryProfileOutcome.NotFound));
    }

    private static (CustomerUnitLinkService Service, RecordingEdsm Edsm) WithStatements(Rig r)
    {
        PactContractDiscovery.ClearCache();
        var edsmOptions = new CollectionsEdsmOptions { EdsmNumberCulture = "en-US" };
        var time = new FakeTimeProvider(Now);
        var edsm = new RecordingEdsm(new TigerCS.Integrations.Modules.CollectionsIntegration.FixtureEdsmCollectionsGateway(Microsoft.Extensions.Options.Options.Create(edsmOptions)));
        var clock = new CollectionsClock(r.Options, time);
        var account = new CollectionsPaymentSummaryAppService(r.Options, edsmOptions, new CollectionsAuthorizationService(r.Options, new FakeDepartmentRepository()), clock, new NoProfiles(), r.Pact, edsm, new PactAccountMappingCache(time));
        var statements = new CollectionsUnitStatementService(edsmOptions, clock, new PactContractDiscovery(r.Pact, time), account, edsm, r.Units, NullLogger<CollectionsUnitStatementService>.Instance);
        var service = new CustomerUnitLinkService(r.Options, new PactReceivablesOptions { Enabled = true }, new ReceivablesSnapshotOptions(), new CollectionsCrmOwnersOptions { Enabled = true },
            new(r.Options, new FakeDepartmentRepository()), clock, r.Crm, r.Pact, r.Map, r.Towers, r.Units, NullLogger<CustomerUnitLinkService>.Instance, new PactContractDiscovery(r.Pact, time), statements, null);
        return (service, edsm);
    }

    [Fact]
    public async Task FullService_AFullyPaidUnit_WithNoPactMobile_GetsItsStatement_EvenWhenPactsContractListIsDown()
    {
        var r = new Rig();
        r.Map.Rows.Add(new(79, "TP140", 4, "Manual"));
        CrmReturns(r, Buyer(498397, "Sreesaran Maru Sudhakar", PhoneE164, "s@example.test", CrmUnit(9090, "909")));
        r.Units.DirectoryComplete = true;
        r.Units.PaidUnits.Add(new(4, "3001", 41230, "TP140-909", 0, 5));          // fully paid: no open instalment, so the open-balance snapshot has nothing for it; tenant 3001 is the fixture's EDSM account
        r.Pact.Result = PactCustomerLookupResult.Unavailable();                    // PACT's contract list is down: it is optional
        var (service, edsm) = WithStatements(r);

        var result = (await service.FindForCrmCustomerAsync(r.Manager, 498397, [Phone], null)).Value!;

        var statement = result.Statement!;
        Assert.True(statement.IsAvailable, statement.Detail);
        Assert.Equal((4, "3001", "TP140-909"), (statement.CompanyId, statement.TenantId, statement.UnitKey));
        Assert.Equal((1250000m, 812500m, 62500m, 375000m), (statement.Total!.Value, statement.Paid!.Value, statement.Due!.Value, statement.Outstanding!.Value));
        Assert.Equal([(4, "3001")], edsm.Summaries);
    }

    [Fact]
    public async Task FullService_AnAccountWithAFullyPaidSibling_ShowsNoStatement_NotTheAccountTotal()
    {
        var r = new Rig();
        r.Map.Rows.Add(new(79, "TP140", 4, "Manual"));
        CrmReturns(r, Buyer(498397, "Owner", PhoneE164, null, CrmUnit(9090, "909")));
        r.Units.DirectoryComplete = true;
        r.Units.Identities.Add(new(4, "3001", 41230, "TP140-909", "Pact Holder", "", "", "140", "T", 3, 3));      // the selected unit still owes money ...
        r.Units.PaidUnits.Add(new(4, "3001", 41231, "TP140-101", 0, 4));                                          // ... its sibling is fully paid, but its payments are inside EDSM's Paid / Total
        r.Units.Instalments.Add(new(4, "3001", 41230, "V1", new(2026, 9, 1), 700m, 700m, 0m, "Installment"));
        var (service, edsm) = WithStatements(r);

        var result = (await service.FindForCrmCustomerAsync(r.Manager, 498397, [Phone], null)).Value!;

        var s = result.Statement!;
        Assert.Equal("NotExclusive", s.Status);
        Assert.Contains("TP140-101 (fully paid)", s.OtherUnits);
        Assert.Null(s.Total); Assert.Null(s.Paid);
        Assert.Empty(edsm.Summaries);                                                // the account was never asked: nothing of it can reach the screen
    }

    [Fact]
    public async Task FullService_WithoutCompleteScopeEvidence_NoAmountIsShown_AndTheMissingQueryIsNamed()
    {
        var r = new Rig();
        r.Map.Rows.Add(new(79, "TP140", 4, "Manual"));
        CrmReturns(r, Buyer(498397, "Owner", PhoneE164, null, CrmUnit(9090, "909")));
        r.Units.Identities.Add(new(4, "3001", 41230, "TP140-909", "Pact Holder", "", "", "140", "T", 3, 3));      // directory NOT accepted as complete (default), contract list down
        r.Pact.Result = PactCustomerLookupResult.Unavailable();
        var (service, edsm) = WithStatements(r);

        var s = (await service.FindForCrmCustomerAsync(r.Manager, 498397, [Phone], null)).Value!.Statement!;

        Assert.Equal("ScopeUnproven", s.Status);
        Assert.Contains("What is missing", s.Detail);
        Assert.Null(s.Total);
        Assert.Empty(edsm.Summaries);
    }

    [Fact]
    public async Task FullService_TwoUnitsOfOneCustomer_NeverShareAmounts()
    {
        var r = new Rig();
        r.Map.Rows.Add(new(79, "TP140", 4, "Manual"));
        CrmReturns(r, Buyer(498397, "Owner", PhoneE164, null, CrmUnit(9090, "909"), CrmUnit(1010, "101")));
        r.Units.DirectoryComplete = true;
        r.Units.Identities.Add(new(4, "3001", 41230, "TP140-909", "Pact Holder", "", "", "140", "T", 3, 3));
        r.Units.Identities.Add(new(4, "4444", 41231, "TP140-101", "Pact Holder", "", "", "140", "T", 3, 3));      // another account
        var (service, edsm) = WithStatements(r);

        var list = (await service.FindForCrmCustomerAsync(r.Manager, 498397, [Phone], null)).Value!;
        Assert.True(list.SelectionRequired);                           // two units: nothing is chosen, so no statement is shown for the pair
        Assert.Null(list.Statement);

        var a = (await service.FindForCrmCustomerAsync(r.Manager, 498397, [Phone], list.Candidates.Single(c => c.UnitKey == "TP140-909").SelectionId)).Value!.Statement!;
        var b = (await service.FindForCrmCustomerAsync(r.Manager, 498397, [Phone], list.Candidates.Single(c => c.UnitKey == "TP140-101").SelectionId)).Value!.Statement!;
        Assert.Equal("3001", a.TenantId); Assert.Equal("4444", b.TenantId);
        Assert.Equal(1250000m, a.Total!.Value);
        Assert.NotEqual(a.Total.Value, b.Total?.Value);
        Assert.Equal([(4, "3001"), (4, "4444")], edsm.Summaries);       // each unit read only its own account
    }

    [Fact]
    public async Task FullService_TwoUnitsOnOneAccount_ShowNoStatement_AndNameTheSourceLimitation()
    {
        var r = new Rig();
        r.Map.Rows.Add(new(79, "TP140", 4, "Manual"));
        CrmReturns(r, Buyer(498397, "Owner", PhoneE164, null, CrmUnit(9090, "909"), CrmUnit(1010, "101")));
        r.Units.DirectoryComplete = true;
        r.Units.Identities.Add(new(4, "3001", 41230, "TP140-909", "Pact Holder", "", "", "140", "T", 3, 3));
        r.Units.Identities.Add(new(4, "3001", 41231, "TP140-101", "Pact Holder", "", "", "140", "T", 3, 3));      // same tenant
        var (service, edsm) = WithStatements(r);
        var list = (await service.FindForCrmCustomerAsync(r.Manager, 498397, [Phone], null)).Value!;

        var s = (await service.FindForCrmCustomerAsync(r.Manager, 498397, [Phone], list.Candidates.Single(c => c.UnitKey == "TP140-909").SelectionId)).Value!.Statement!;

        Assert.Equal("NotExclusive", s.Status);
        Assert.Contains("TP140-101", s.OtherUnits);
        Assert.Contains("no unit parameter", s.Detail);
        Assert.Empty(edsm.Summaries);
    }

    [Fact]
    public async Task FullService_Volga3302_ResolvesFromTheProjectCodeCrmPublishes_WithoutAMappingRowOrAnyPactMobile()
    {
        var r = new Rig();
        CrmReturns(r, Buyer(437381, "BEN ISRAELI", PhoneE164, null, CrmUnit(3302, "3302", project: 94, projectName: "VOLGA Tower") with { ProjectCode = "TP125" }));
        r.Units.Identities.Add(new(4, "3001", 43302, "TP125-3302", "Pact Holder", "", "", "125", "VOLGA", 3, 3));
        r.Units.Instalments.Add(new(4, "3001", 43302, "V1", new(2026, 9, 1), 900m, 900m, 0m, "Installment"));
        var (service, edsm) = WithStatements(r);

        var unit = Assert.Single((await service.FindForCrmCustomerAsync(r.Manager, 437381, [Phone], null)).Value!.Candidates);

        Assert.Equal(("TP125-3302", 4), (unit.UnitKey, unit.CompanyId));
        Assert.NotEqual("MatchFailed", unit.FinancialStatus);
        Assert.Equal("Available", unit.FinancialStatus);                    // resolved from CRM's own project code + the exact unit code in PACT's unit data: no mapping row, no contract-list evidence
        
        _ = edsm;
    }

    [Fact]
    public async Task FullService_AStarredPactUnit_IsNeverMatchedToTheCrmUnit_AndGetsNoStatement()
    {
        var r = new Rig();
        r.Map.Rows.Add(new(79, "TP140", 4, "Manual"));
        CrmReturns(r, Buyer(498397, "Owner", PhoneE164, null, CrmUnit(9090, "909")));
        r.Units.Identities.Add(new(4, "3001", 41230, "TP140-909*", "Pact Holder", "", "", "140", "T", 3, 3));    // only the cancelled code exists in PACT
        var (service, edsm) = WithStatements(r);
        var result = (await service.FindForCrmCustomerAsync(r.Manager, 498397, [Phone], null)).Value!;
        Assert.DoesNotContain(result.Candidates, c => c.FinancialStatus == "Available");
        Assert.True(result.Statement is null || !result.Statement.IsAvailable);
        Assert.Empty(edsm.Summaries);
    }
}

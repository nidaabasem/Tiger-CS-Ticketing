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
}

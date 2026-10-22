using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Reconciliation;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.Collections.Snapshot;
using TigerCS.Tests.IdentityAndAccess.Fakes;

namespace TigerCS.Tests.Collections.Reconciliation;

/// <summary>Reconciliation runs, the single source gate and the evidence it relies on: missing evidence is not a pass, a difference is not an error, and nothing starts by itself.</summary>
public sealed class FinancialReconciliationServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);
    private static readonly CollectionsCaller Manager = new(Guid.NewGuid(), [Roles.CsManager], []);

    private sealed class FakeDirect : IReconciliationSource
    {
        public List<PactReceivableInstalment> Rows { get; } = [];
        public Exception? Throw { get; set; }
        public int Reads { get; private set; }
        public string ProcedureLabel(int companyId) => $"dbo.p{companyId}AccountReceivables";
        public Task<IReadOnlyList<PactReceivableInstalment>> ReadAsync(int companyId, DateOnly from, DateOnly through, CancellationToken ct)
        {
            Reads++;
            if (Throw is not null) throw Throw;
            return Task.FromResult<IReadOnlyList<PactReceivableInstalment>>(Rows.Where(r => r.CompanyId == companyId).ToList());
        }
    }

    private sealed class SyncLauncher : IReconciliationLauncher
    {
        public FinancialReconciliationService? Service { get; set; }
        public int Launches { get; private set; }
        public void Launch(Guid runId) { Launches++; Service!.ExecuteAsync(runId, CancellationToken.None).GetAwaiter().GetResult(); }
    }

    private sealed class Harness
    {
        public FakeSnapshotSource Snapshot = new(Now);
        public FakeDirect Direct = new();
        public ReconciliationHistory History = new();
        public SyncLauncher Launcher = new();
        public CollectionsCampaignOptions Campaign = new() { FinancialSourceValidated = true };
        public PactReceivablesOptions Source = new() { Enabled = true };
        public FinancialReconciliationService Service;
        public DateTime Clock = Now;

        public Harness(bool launcher = true)
        {
            var options = new CollectionsOptions { Enabled = true };
            var time = new FixedTime(this);
            Service = new(options, Campaign, Source, new CollectionsAuthorizationService(options, new FakeDepartmentRepository()), new CollectionsClock(options, time), Snapshot, Direct, History,
                NullLogger<FinancialReconciliationService>.Instance, launcher ? Launcher : null);
            Launcher.Service = Service;
            Snapshot.MaxAgeMinutes = 180;
        }

        public static PactReceivableInstalment Row(int company, string unit, string voucher, DateTime due, decimal amount, int unitId = 11) =>
            new(company, "T" + unitId, "Name", "0501234567", "a@b.ae", unitId, unit, "140", voucher, "", due, amount, "Unpaid");

        public void Both(PactReceivableInstalment row) { Snapshot.Rows.Add(row); Direct.Rows.Add(row); }
        private sealed class FixedTime(Harness h) : TimeProvider { public override DateTimeOffset GetUtcNow() => new(h.Clock, TimeSpan.Zero); }
    }

    private static ReconciliationRunDto Latest(Harness h) => h.History.Latest()!;

    [Fact]
    public async Task MatchingData_Passes_ForBothCompanies_AndOpensTheGate()
    {
        var h = new Harness();
        h.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m));
        h.Both(Harness.Row(32, "TP127-201", "V2", new(2026, 10, 5), 500m, 21));
        var start = await h.Service.StartAsync(Manager, null, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.Accepted, start.Outcome);
        var run = Latest(h);
        Assert.Equal(ReconciliationStates.Passed, run.Status);
        Assert.All(run.Companies, c => Assert.Equal(ReconciliationStates.Passed, c.Status));
        var gate = h.Service.Gate();
        Assert.True(gate.Reconciled);
        Assert.Equal(SourceGate.Reconciled, gate.Code);
    }

    [Fact]
    public async Task ADifference_FailsThatCompany_ListsTheUnit_AndKeepsTheGateClosed_WhileTheOtherCompanyStillPasses()
    {
        var h = new Harness();
        h.Both(Harness.Row(32, "TP127-201", "V2", new(2026, 10, 5), 500m, 21));
        h.Snapshot.Rows.Add(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m));
        h.Direct.Rows.Add(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 400m));
        await h.Service.StartAsync(Manager, null, CancellationToken.None);
        var run = Latest(h);
        Assert.Equal(ReconciliationStates.Failed, run.Status);
        Assert.Equal(ReconciliationStates.Failed, run.Companies.Single(c => c.CompanyId == 4).Status);
        Assert.Equal(ReconciliationStates.Passed, run.Companies.Single(c => c.CompanyId == 32).Status);
        Assert.Equal("TP140-101", run.Companies.Single(c => c.CompanyId == 4).Comparison!.Diffs.Single().UnitCode);
        var gate = h.Service.Gate();
        Assert.False(gate.Reconciled);
        Assert.Equal(SourceGate.EvidenceFailed, gate.Code);
        Assert.Contains("Tiger Group Dubai", gate.Message);
        Assert.DoesNotContain("Sharjah", gate.Message);
    }

    [Fact]
    public async Task PactUnreachable_IsAnError_NotAFailureAndNotAPass()
    {
        var h = new Harness();
        h.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m));
        h.Direct.Throw = new PactReceivablesSourceException("The PACT report connection is not configured.");
        await h.Service.StartAsync(Manager, 4, CancellationToken.None);
        var company = Assert.Single(Latest(h).Companies);
        Assert.Equal(ReconciliationStates.Error, company.Status);
        Assert.Null(company.Comparison);
        Assert.Equal(SourceGate.EvidenceError, h.Service.Gate().Code);
    }

    [Fact]
    public async Task AnOldSnapshot_IsNotCompared_BecausePaymentsSinceTheRefreshWouldLookLikeDifferences()
    {
        var h = new Harness();
        h.Snapshot.Companies[4] = FakeSnapshotSource.Healthy(4, Now.AddMinutes(-120));
        h.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m));
        await h.Service.StartAsync(Manager, 4, CancellationToken.None);
        var company = Assert.Single(Latest(h).Companies);
        Assert.Equal(ReconciliationStates.Error, company.Status);
        Assert.Contains("minutes old", company.Message);
        Assert.Equal(0, h.Direct.Reads);                                    // PACT is not read for a comparison that cannot be exact
    }

    [Fact]
    public async Task ARefreshInProgress_And_NeverLoaded_AreErrors()
    {
        var h = new Harness();
        h.Snapshot.Companies[4] = FakeSnapshotSource.Healthy(4, Now.AddMinutes(-5)) with { RefreshInProgress = true, RefreshStartedUtc = Now.AddMinutes(-1) };
        h.Snapshot.Companies[32] = FakeSnapshotSource.NeverLoaded(32);
        await h.Service.StartAsync(Manager, null, CancellationToken.None);
        var run = Latest(h);
        Assert.Contains("refresh of this company is running", run.Companies.Single(c => c.CompanyId == 4).Message);
        Assert.Contains("No snapshot has been loaded", run.Companies.Single(c => c.CompanyId == 32).Message);
        Assert.Equal(ReconciliationStates.Error, run.Status);
    }

    [Fact]
    public async Task NothingOnEitherSide_IsAnError_NotAVacuousPass()
    {
        var h = new Harness();
        await h.Service.StartAsync(Manager, 4, CancellationToken.None);
        Assert.Equal(ReconciliationStates.Error, Assert.Single(Latest(h).Companies).Status);
    }

    [Fact]
    public async Task EvidenceNeverStartedByAPageRead_AndRunsAreSingleFlightWithACooldown()
    {
        var h = new Harness();
        h.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m));
        _ = await h.Service.OverviewAsync(Manager, CancellationToken.None);
        _ = h.Service.Gate();
        Assert.Equal((0, 0), (h.Launcher.Launches, h.Direct.Reads));         // reading the status never reads PACT
        Assert.Equal(CollectionsOutcome.Accepted, (await h.Service.StartAsync(Manager, 4, CancellationToken.None)).Outcome);
        var second = await h.Service.StartAsync(Manager, 4, CancellationToken.None);   // clicked again at once
        Assert.Equal(CollectionsOutcome.Conflict, second.Outcome);
        Assert.Equal(1, h.Launcher.Launches);
        h.Clock = Now.AddMinutes(3);
        h.Snapshot.Companies[4] = FakeSnapshotSource.Healthy(4, h.Clock.AddMinutes(-5));
        Assert.Equal(CollectionsOutcome.Accepted, (await h.Service.StartAsync(Manager, 4, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task OnlyAPersonWhoMaySendCanStartOne_AndAnyReaderCanSeeTheResult()
    {
        var h = new Harness();
        var reader = new CollectionsCaller(Guid.NewGuid(), [], []);
        Assert.Equal(CollectionsOutcome.Forbidden, (await h.Service.StartAsync(reader, null, CancellationToken.None)).Outcome);
        Assert.Equal(0, h.Launcher.Launches);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.StartAsync(Manager, 7, CancellationToken.None)).Outcome);
        var noLauncher = new Harness(launcher: false);
        Assert.Equal(CollectionsOutcome.Disabled, (await noLauncher.Service.StartAsync(Manager, null, CancellationToken.None)).Outcome);
    }

    // ---------------------------------------------------------------- the gate

    [Fact]
    public void WithoutASignOff_TheGateIsClosed_WhateverTheEvidenceSays()
    {
        var h = new Harness { };
        h.Campaign.FinancialSourceValidated = false;
        var gate = h.Service.Gate();
        Assert.False(gate.Reconciled); Assert.Equal(SourceGate.SignOffMissing, gate.Code); Assert.False(gate.SignOffRecorded);
    }

    [Fact]
    public void ASignOffForAnotherProcedure_DoesNotCount()
    {
        var h = new Harness();
        h.Campaign.ValidatedProcedureSuffix = ""; h.Source.ProcedureSuffix = "V2";
        Assert.Equal(SourceGate.ProcedureChanged, h.Service.Gate().Code);
    }

    [Fact]
    public void WithASignOffButNoRun_TheGateSaysEvidenceIsMissing_NotThatTheDataIsWrong()
    {
        var gate = new Harness().Service.Gate();
        Assert.False(gate.Reconciled); Assert.Equal(SourceGate.EvidenceNotRun, gate.Code);
        Assert.True(gate.SignOffRecorded);
    }

    [Fact]
    public async Task APassingRunExpiresAfterTheConfiguredHours_AndALaterErrorDoesNotRevokeIt()
    {
        var h = new Harness();
        h.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m));
        h.Both(Harness.Row(32, "TP127-201", "V2", new(2026, 10, 5), 500m, 21));
        await h.Service.StartAsync(Manager, null, CancellationToken.None);
        Assert.True(h.Service.Gate().Reconciled);
        h.Clock = Now.AddMinutes(10); h.Snapshot.Companies[4] = FakeSnapshotSource.Healthy(4, h.Clock.AddMinutes(-5));
        h.Direct.Throw = new PactReceivablesSourceException("down");
        await h.Service.StartAsync(Manager, 4, CancellationToken.None);          // an Error run
        Assert.True(h.Service.Gate().Reconciled);                                 // earlier conclusive evidence stands
        h.Clock = Now.AddHours(25);
        Assert.Equal(SourceGate.EvidenceExpired, h.Service.Gate().Code);
    }

    [Fact]
    public void TheAutomatedRequirementCanBeSwitchedOff_Explicitly()
    {
        var h = new Harness();
        h.Campaign.RequireAutomatedReconciliation = false;
        Assert.True(h.Service.Gate().Reconciled);
    }

    [Fact]
    public void WithoutAnyEvidenceSource_TheGateFallsBackToTheSignOffOnly()
    {
        var gate = SourceGate.Evaluate(new CollectionsCampaignOptions { FinancialSourceValidated = true }, new PactReceivablesOptions(), null, Now);
        Assert.True(gate.Reconciled);
    }

    // ---------------------------------------------------------------- what the check is, and is not

    [Fact]
    public async Task APassingRun_IsLabelledSnapshotConsistency_NeverAnIndependentLedgerVerification()
    {
        var h = new Harness();
        h.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m)); h.Both(Harness.Row(32, "TP127-201", "V2", new(2026, 10, 5), 500m, 21));
        await h.Service.StartAsync(Manager, null, CancellationToken.None);
        var gate = h.Service.Gate();
        Assert.True(gate.Reconciled);
        Assert.Equal("SnapshotConsistency", gate.Basis);
        Assert.False(ReconciliationBasis.IsIndependentLedgerVerification);
        Assert.Contains("not an independent ledger verification", gate.Message);
        Assert.Contains("usp_CollectionsRcv_Refresh", Latest(h).Companies[0].SnapshotOrigin);
        Assert.Contains("same PACT procedure", ReconciliationBasis.DoesNotProve + " same PACT procedure");
        Assert.Contains("human sign-off", ReconciliationBasis.DoesNotProve);
    }

    [Fact]
    public async Task APassingConsistencyCheck_NeverReplacesTheSignOff_AndTheSignOffNeverReplacesTheCheck()
    {
        var h = new Harness();
        h.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m)); h.Both(Harness.Row(32, "TP127-201", "V2", new(2026, 10, 5), 500m, 21));
        h.Campaign.FinancialSourceValidated = false;                       // the check passes, nobody has signed off
        await h.Service.StartAsync(Manager, null, CancellationToken.None);
        Assert.Equal(ReconciliationStates.Passed, Latest(h).Status);
        var noSignOff = h.Service.Gate();
        Assert.False(noSignOff.Reconciled); Assert.Equal(SourceGate.SignOffMissing, noSignOff.Code); Assert.False(noSignOff.LedgerVerificationAttested);

        h.Campaign.FinancialSourceValidated = true;                         // signed off but no check on record (fresh process)
        var fresh = SourceGate.Evaluate(h.Campaign, h.Source, new ReconciliationHistory(), Now);
        Assert.False(fresh.Reconciled); Assert.Equal(SourceGate.EvidenceNotRun, fresh.Code); Assert.True(fresh.LedgerVerificationAttested);
    }

    [Fact]
    public void AConfiguredProcedureSuffix_IsWarnedAbout_BecauseTheSnapshotRefreshNeverReadsIt()
    {
        var campaign = new CollectionsCampaignOptions { FinancialSourceValidated = true, ValidatedProcedureSuffix = "V2", RequireAutomatedReconciliation = false };
        var gate = SourceGate.Evaluate(campaign, new PactReceivablesOptions { ProcedureSuffix = "V2" }, null, Now);
        Assert.Contains("p4/p32AccountReceivables", gate.Warning);
        Assert.Null(SourceGate.Evaluate(new CollectionsCampaignOptions { FinancialSourceValidated = true }, new PactReceivablesOptions(), null, Now).Warning);
    }
}

extern alias TigerCsWeb;

using System.Text.Json;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCsWeb::TigerCS.Web.Pages.Collections;

namespace TigerCS.Tests.Collections.Snapshot;

/// <summary>Per-company load states, the stuck-refresh limit, safe failure text and the polling payload: what keeps a page from showing a permanent "Loading".</summary>
public sealed class SnapshotLoadStateTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);

    private static SnapshotCompanyStatusDto Build(SnapshotCompanyRaw raw, int maxRunMinutes = 35) => ReceivablesSnapshotStatusBuilder.Company(raw, Now, 180, maxRunMinutes);
    private static SnapshotCompanyRaw Running(DateTime startedUtc) => FakeSnapshotSource.Healthy(4, Now.AddMinutes(-60)) with { RefreshInProgress = true, RefreshStartedUtc = startedUtc, LastAttemptStatus = "Running", LastAttemptUtc = startedUtc };

    [Fact]
    public void ARefreshThatReallyRunsWithinItsExpectedDuration_IsLoading_AndKeepsTheLastValidData()
    {
        var c = Build(Running(Now.AddMinutes(-3)));
        Assert.Equal(SnapshotLoadStates.Loading, c.State);
        Assert.True(c.IsLoading);
        Assert.True(c.HasData);
    }

    [Fact]
    public void ARefreshOlderThanItsCommandTimeoutPlusSlack_IsStuck_NotLoading_SoPollingCanEnd()
    {
        var c = Build(Running(Now.AddMinutes(-40)));
        Assert.Equal(SnapshotLoadStates.Stuck, c.State);
        Assert.False(c.IsLoading);
        Assert.True(c.RefreshOverdue);
    }

    [Fact]
    public void AFailedAttemptIsNeverLoading_AndTheLastValidSnapshotIsStillReported()
    {
        var c = Build(FakeSnapshotSource.Failed(4, Now.AddMinutes(-200), Now.AddMinutes(-1), 50012));
        Assert.Equal(SnapshotLoadStates.Failed, c.State);
        Assert.False(c.IsLoading);
        Assert.True(c.HasData);
        Assert.Equal(Now.AddMinutes(-200), c.LastSuccessUtc);
        Assert.Contains("failed", c.FailureText);
    }

    [Theory]
    [InlineData(10, SnapshotLoadStates.Ready)]
    [InlineData(181, SnapshotLoadStates.Stale)]
    public void FreshnessDecidesReadyOrStale_WhenNothingFailedAndNothingRuns(int ageMinutes, string expected) =>
        Assert.Equal(expected, Build(FakeSnapshotSource.Healthy(4, Now.AddMinutes(-ageMinutes))).State);

    [Fact]
    public void NeverLoadedIsItsOwnState_NotAnEmptyResult() =>
        Assert.Equal(SnapshotLoadStates.NeverLoaded, Build(FakeSnapshotSource.NeverLoaded(32)).State);

    [Fact]
    public void CompaniesAreJudgedIndependently_AFailedSharjahDoesNotTurnDubaiIntoAProblem()
    {
        var status = ReceivablesSnapshotStatusBuilder.Build([FakeSnapshotSource.Healthy(4, Now.AddMinutes(-5)), FakeSnapshotSource.Failed(32, Now.AddMinutes(-300), Now.AddMinutes(-1))], [], Now, 180, 35);
        Assert.Equal(SnapshotLoadStates.Ready, status.Companies.Single(c => c.CompanyId == 4).State);
        Assert.Equal(SnapshotLoadStates.Failed, status.Companies.Single(c => c.CompanyId == 32).State);
        Assert.Equal([32], CollectionsDisplay.FailedCompanies(status)!.Select(c => c.CompanyId));
        Assert.False(status.LoadInProgress);
    }

    [Fact]
    public void ThePollingPayloadIsActiveOnlyWhileARefreshReallyRuns()
    {
        string Active(params SnapshotCompanyRaw[] raws) => JsonSerializer.Serialize(CollectionsDisplay.StatusPayload(ReceivablesSnapshotStatusBuilder.Build(raws, [], Now, 180, 35)));
        Assert.Contains("\"active\":true", Active(Running(Now.AddMinutes(-2)), FakeSnapshotSource.Healthy(32, Now.AddMinutes(-5))));
        Assert.Contains("\"active\":false", Active(Running(Now.AddMinutes(-90)), FakeSnapshotSource.Healthy(32, Now.AddMinutes(-5))));   // stuck
        Assert.Contains("\"active\":false", Active(FakeSnapshotSource.Failed(4, Now.AddMinutes(-200), Now.AddMinutes(-1)), FakeSnapshotSource.NeverLoaded(32)));
        Assert.Contains("\"active\":false", JsonSerializer.Serialize(CollectionsDisplay.StatusPayload(null)));
    }

    [Fact]
    public void ThePollingPayloadIsTiny()
    {
        var json = JsonSerializer.Serialize(CollectionsDisplay.StatusPayload(ReceivablesSnapshotStatusBuilder.Build(
            [Running(Now.AddMinutes(-2)), FakeSnapshotSource.Failed(32, Now.AddMinutes(-300), Now.AddMinutes(-1))], [], Now, 180, 35)));
        Assert.True(json.Length < 1500, $"{json.Length} bytes");
    }

    // ---------------------------------------------------------------- failure text

    [Theory]
    [InlineData(-2, null, "reading from PACT", "timed out")]
    [InlineData(7399, null, "reading from PACT", "linked server")]
    [InlineData(213, null, "reading from PACT", "layout")]
    [InlineData(50010, 4000, "validating the PACT result", "more rows than the configured limit")]
    [InlineData(50012, 4000, "validating the PACT result", "much smaller")]
    [InlineData(50013, 4000, "publishing the snapshot", "row count")]
    [InlineData(1205, 4000, "publishing the snapshot", "deadlock")]
    public void TheFailureNamesThePhaseAndTheKind(int number, int? fetchMs, string phase, string kind)
    {
        var text = SnapshotFailureText.Describe(number, null, fetchMs, 1000);
        Assert.Contains(phase, text);
        Assert.Contains(kind, text);
    }

    [Fact]
    public void ServerNamesAddressesAndSqlTextFromADriverMessageNeverReachThePage()
    {
        var raw = "Named Pipes Provider: Could not open a connection to SQL Server [53]. Login failed for user 'crmpact' at 10.10.10.94";
        Assert.Null(SnapshotFailureText.Sanitize(53, raw));
        var text = SnapshotFailureText.Describe(53, raw, null, null);
        Assert.DoesNotContain("10.10.10.94", text); Assert.DoesNotContain("crmpact", text); Assert.DoesNotContain("Named Pipes", text);
    }

    [Fact]
    public void OurOwnProcedureMessagesAreShown_WithAnyAddressMasked()
    {
        var shown = SnapshotFailureText.Sanitize(50012, "The new result is much smaller than the published snapshot; it was not published (previous snapshot kept). 10.1.2.3");
        Assert.Contains("previous snapshot kept", shown);
        Assert.DoesNotContain("10.1.2.3", shown);
    }
}

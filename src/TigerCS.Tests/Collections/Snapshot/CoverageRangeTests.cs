using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Snapshot;

/// <summary>Custom From/To ranges, "Last 6 months", coverage gaps, the load action and the export block.</summary>
public sealed class CoverageRangeTests
{
    private static readonly DateTime Now = new(2026, 3, 15, 8, 0, 0, DateTimeKind.Utc);   // 15 Mar 2026 (Dubai noon)

    [Theory]
    [InlineData(2026, 3, 15, 2025, 9, 15)]    // crosses the year boundary
    [InlineData(2026, 10, 14, 2026, 4, 14)]
    [InlineData(2026, 8, 31, 2026, 2, 28)]    // shorter target month clamps
    [InlineData(2026, 1, 1, 2025, 7, 1)]
    public void LastSixMonthsIsSixCalendarMonthsBeforeThePreviewDateThroughThePreviewDate(int y, int m, int d, int fy, int fm, int fd)
    {
        var (from, to) = CollectionsDateRanges.LastSixMonths(new DateOnly(y, m, d));
        Assert.Equal((new DateOnly(fy, fm, fd), new DateOnly(y, m, d)), (from, to));
    }

    [Fact]
    public void JanuaryFirstIsOnlyADefault_AnyRangeInTheSupportedYearsIsAccepted()
    {
        Assert.True(CollectionsDateRanges.IsSupported(new DateOnly(2018, 4, 9), new DateOnly(2026, 1, 3)));
        Assert.True(CollectionsDateRanges.IsSupported(new DateOnly(2025, 9, 15), new DateOnly(2026, 3, 15)));
        Assert.True(CollectionsDateRanges.IsSupported(new DateOnly(2026, 5, 5), new DateOnly(2026, 5, 5)));
        Assert.False(CollectionsDateRanges.IsSupported(new DateOnly(2026, 5, 6), new DateOnly(2026, 5, 5)));
        Assert.False(CollectionsDateRanges.IsSupported(new DateOnly(1999, 12, 31), new DateOnly(2026, 1, 1)));
    }

    private static readonly DateOnly From = new(2025, 9, 15), To = new(2026, 3, 15);
    private static SnapshotCompanyRaw Covered(int company, string from, string to) =>
        FakeSnapshotSource.Healthy(company, Now.AddMinutes(-5)) with { CoverageFrom = DateOnly.Parse(from), CoverageThrough = DateOnly.Parse(to) };

    [Fact]
    public void ComposeReportsEveryGap_AndDoesNotTruncateOrZeroTheRange()
    {
        var snapshot = ReceivablesSnapshotComposer.Compose(From, To, [Covered(4, "2025-11-01", "2026-02-28"), Covered(32, "2025-01-01", "2099-12-31")], [], [], Now, 90).Snapshot!;
        Assert.False(snapshot.RangeCovered);
        Assert.Equal([(4, new DateOnly(2025, 9, 15), new DateOnly(2025, 10, 31)), (4, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 15))],
            snapshot.Gaps.Select(g => (g.CompanyId, g.From, g.Through)));
        Assert.False(snapshot.IsReady);
        Assert.True(snapshot.IsFresh);                       // fresh data, but not a complete answer
        Assert.Contains("not fully loaded", snapshot.ReadyProblem);
        Assert.Equal((From, To), (snapshot.RequestedFrom, snapshot.RequestedThrough));
    }

    [Fact]
    public void FullyCoveredRangeHasNoGaps_AndOneDayOutsideIsAGap()
    {
        Assert.True(ReceivablesSnapshotComposer.Compose(From, To, [Covered(4, "2025-09-15", "2026-03-15")], [], [], Now, 90).Snapshot!.IsReady);
        var gap = ReceivablesSnapshotComposer.Compose(From, To, [Covered(4, "2025-09-16", "2026-03-15")], [], [], Now, 90).Snapshot!.Gaps.Single();
        Assert.Equal((new DateOnly(2025, 9, 15), new DateOnly(2025, 9, 15)), (gap.From, gap.Through));
    }

    [Fact]
    public void ARangeEntirelyOutsideCoverageIsOneWholeGap()
    {
        var gap = ReceivablesSnapshotComposer.Compose(From, To, [Covered(4, "2026-06-01", "2026-12-31")], [], [], Now, 90).Snapshot!.Gaps.Single();
        Assert.Equal((From, To), (gap.From, gap.Through));
    }

    private sealed class Loader(bool starts = true) : IReceivablesRangeLoader
    {
        public List<(DateOnly, DateOnly)> Requests { get; } = [];
        public Task<bool> EnqueueAsync(DateOnly from, DateOnly through, CancellationToken cancellationToken) { Requests.Add((from, through)); return Task.FromResult(starts); }
        public List<int> CompanyRequests { get; } = [];
        public Task<bool> EnqueueCompanyRefreshAsync(int companyId, CancellationToken cancellationToken) { CompanyRequests.Add(companyId); return Task.FromResult(starts); }
    }

    private sealed class Harness
    {
        public FakeSnapshotSource Source { get; } = new(Now);
        public Loader Loader { get; } = new();
        public PactReceivableCustomersAppService Receivables { get; }
        public CollectionsCampaignAppService Campaigns { get; }
        public CollectionsCaller Manager { get; } = new(Guid.NewGuid(), [Roles.CsManager], []);

        public Harness(Loader? loader = null)
        {
            Loader = loader ?? Loader;
            var options = new CollectionsOptions { Enabled = true };
            var auth = new CollectionsAuthorizationService(options, new FakeDepartmentRepository());
            var clock = new CollectionsClock(options, new FakeTimeProvider(Now));
            Receivables = new(options, new PactReceivablesOptions { Enabled = true, DefaultMinOutstandingAmount = 0m }, auth, clock, Source, NullLogger<PactReceivableCustomersAppService>.Instance, null, Loader);
            Campaigns = new(options, new CollectionsCampaignOptions { FinancialSourceValidated = true }, new PactReceivablesOptions { Enabled = true, DefaultMinOutstandingAmount = 0m },
                new(options, new FakeDepartmentRepository()), clock, Source, NullLogger<CollectionsCampaignAppService>.Instance);
        }
    }

    private static PactReceivableInstalment Row(string tenant, DateTime due, int unitId = 1, decimal amount = 100m) =>
        new(4, tenant, "Customer " + tenant, "971500003001", "x@example.test", unitId, "TP124-" + unitId, "", "V" + tenant, "", due, amount, "Installment");

    [Fact]
    public async Task CustomRangeAcrossTheYearBoundaryIsAnswered_WithRowsFromBothYears()
    {
        var h = new Harness();
        h.Source.Rows.AddRange([Row("a", new DateTime(2025, 9, 15), 1), Row("b", new DateTime(2025, 12, 31, 23, 0, 0), 2), Row("c", new DateTime(2026, 1, 1), 3),
            Row("d", new DateTime(2026, 3, 15), 4), Row("e", new DateTime(2025, 9, 14), 5), Row("f", new DateTime(2026, 3, 16), 6)]);
        var (from, to) = CollectionsDateRanges.LastSixMonths(new DateOnly(2026, 3, 15));
        var list = (await h.Receivables.ListAsync(h.Manager, dateFrom: from, dateTo: to)).Value!;
        Assert.Equal(["a", "b", "c", "d"], list.Items.Select(c => c.TenantId).Order());
        Assert.Equal((new DateOnly(2025, 9, 15), new DateOnly(2026, 3, 15)), (list.DateFrom, list.DateTo));
        Assert.True(list.Snapshot!.RangeCovered);
        // Existing rules untouched: everything before March 2026 is Overdue, the 15 Mar instalment is Due (current month).
        Assert.Equal("OverDue", list.Items.Single(c => c.TenantId == "a").Instalments[0].ReceivablesType);
        Assert.Equal("Due", list.Items.Single(c => c.TenantId == "d").Instalments[0].ReceivablesType);
    }

    [Fact]
    public async Task AnEarlierFromThanJanuaryFirstIsNotRejected()
    {
        var h = new Harness();
        h.Source.Rows.Add(Row("old", new DateTime(2019, 6, 30)));
        Assert.Equal("old", Assert.Single((await h.Receivables.ListAsync(h.Manager, dateFrom: new DateOnly(2019, 1, 1))).Value!.Items).TenantId);
    }

    [Fact]
    public async Task PartialCoverage_IsReturnedAsIncompleteWithTheGap_NotAsAnErrorOrAFreshEmptyList()
    {
        var h = new Harness();
        h.Source.Companies[4] = Covered(4, "2026-01-01", "2099-12-31");
        h.Source.Companies[32] = Covered(32, "2026-01-01", "2099-12-31");
        h.Source.Rows.Add(Row("covered", new DateTime(2026, 2, 10)));
        var list = (await h.Receivables.ListAsync(h.Manager, dateFrom: From, dateTo: To)).Value!;
        Assert.Equal("covered", Assert.Single(list.Items).TenantId);
        Assert.False(list.Snapshot!.RangeCovered);
        Assert.Equal([4, 32], list.Snapshot.Gaps.Select(g => g.CompanyId).Order());
        Assert.All(list.Snapshot.Gaps, g => Assert.Equal((new DateOnly(2025, 9, 15), new DateOnly(2025, 12, 31)), (g.From, g.Through)));
    }

    [Fact]
    public async Task CampaignExportIsBlockedUntilTheWholeRangeIsCovered_ThenAllowed()
    {
        var h = new Harness();
        h.Source.Companies[4] = Covered(4, "2026-01-01", "2099-12-31");
        h.Source.Companies[32] = Covered(32, "2026-01-01", "2099-12-31");
        h.Source.Rows.Add(Row("c", new DateTime(2026, 3, 20)));
        var preview = (await h.Campaigns.PreviewAsync(h.Manager, "CurrentMonthReminder", dateFrom: From, dateTo: new DateOnly(2026, 3, 31))).Value!;
        Assert.All(preview.Items, c => { Assert.Equal("NeedsReview", c.Status); Assert.Contains("CoverageIncomplete", c.Reason); });
        foreach (var mode in new[] { "review", "genesys" })
        {
            var blocked = await h.Campaigns.ExportAsync(h.Manager, "CurrentMonthReminder", mode, dateFrom: From, dateTo: new DateOnly(2026, 3, 31));
            Assert.Equal(CollectionsOutcome.InvalidRequest, blocked.Outcome);
            Assert.Contains("not ready to export", blocked.Detail);
            Assert.Contains("Load the missing data", blocked.Detail);
        }
        // A range inside the coverage exports normally; so does the same range once coverage is extended.
        Assert.True((await h.Campaigns.ExportAsync(h.Manager, "CurrentMonthReminder", "review", dateFrom: new DateOnly(2026, 1, 1), dateTo: new DateOnly(2026, 3, 31))).IsSuccess);
        h.Source.Companies[4] = Covered(4, "2000-01-01", "2099-12-31");
        h.Source.Companies[32] = Covered(32, "2000-01-01", "2099-12-31");
        Assert.True((await h.Campaigns.ExportAsync(h.Manager, "CurrentMonthReminder", "review", dateFrom: From, dateTo: new DateOnly(2026, 3, 31))).IsSuccess);
    }

    [Fact]
    public async Task ExportOfACoveredButStaleRangeIsStillBlocked()
    {
        var h = new Harness();
        h.Source.Companies[4] = FakeSnapshotSource.Healthy(4, Now.AddMinutes(-200));
        h.Source.Rows.Add(Row("c", new DateTime(2026, 3, 20)));
        Assert.False((await h.Campaigns.ExportAsync(h.Manager, "CurrentMonthReminder", "review", dateFrom: From, dateTo: new DateOnly(2026, 3, 31))).IsSuccess);
    }

    [Fact]
    public async Task LoadRequest_StartsABackgroundLoadOfExactlyTheRequestedRange()
    {
        var h = new Harness();
        h.Source.Companies[4] = Covered(4, "2026-01-01", "2099-12-31");
        h.Source.Companies[32] = Covered(32, "2026-01-01", "2099-12-31");
        var result = (await h.Receivables.RequestLoadAsync(h.Manager, From, To)).Value!;
        Assert.True(result.Accepted);
        Assert.Equal((From, To), Assert.Single(h.Loader.Requests));
    }

    [Fact]
    public async Task LoadRequest_IsRefusedForReportingUsersAndInvalidRanges_AndSkippedWhenAlreadyCovered()
    {
        var h = new Harness();
        Assert.Equal(CollectionsOutcome.Forbidden, (await h.Receivables.RequestLoadAsync(new CollectionsCaller(Guid.NewGuid(), [Roles.ReportingUser], []), From, To)).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Receivables.RequestLoadAsync(h.Manager, To, From)).Outcome);
        var covered = (await h.Receivables.RequestLoadAsync(h.Manager, From, To)).Value!;   // FakeSnapshotSource default coverage is 2000..2099 and fresh
        Assert.True(covered.AlreadyCovered);
        Assert.Empty(h.Loader.Requests);
    }

    [Fact]
    public async Task LoadRequest_WhenNothingWasEverLoaded_StillStartsTheLoad_AndAnOverlapIsReportedNotDuplicated()
    {
        var h = new Harness();
        h.Source.Companies[4] = FakeSnapshotSource.NeverLoaded(4);
        h.Source.Companies[32] = FakeSnapshotSource.NeverLoaded(32);
        Assert.True((await h.Receivables.RequestLoadAsync(h.Manager, From, To)).Value!.Accepted);
        var busy = new Harness(new Loader(starts: false));
        busy.Source.Companies[4] = FakeSnapshotSource.NeverLoaded(4);
        busy.Source.Companies[32] = FakeSnapshotSource.NeverLoaded(32);
        Assert.True((await busy.Receivables.RequestLoadAsync(busy.Manager, From, To)).Value!.AlreadyRunning);
    }

    [Fact]
    public async Task LoadRequest_IsNotRepeatedWhileALoadIsRunning()
    {
        var h = new Harness();
        h.Source.Companies[4] = Covered(4, "2026-01-01", "2099-12-31") with { RefreshInProgress = true };
        h.Source.Companies[32] = Covered(32, "2026-01-01", "2099-12-31");
        Assert.True((await h.Receivables.RequestLoadAsync(h.Manager, From, To)).Value!.AlreadyRunning);
        Assert.Empty(h.Loader.Requests);
    }

    [Fact]
    public async Task Retry_OfOneCompany_RefreshesOnlyThatCompany_EvenWhenItsDatesLookCovered()
    {
        var h = new Harness();   // Sharjah loaded a while ago and its latest refresh failed: the range is "covered" but not ready
        h.Source.Companies[32] = FakeSnapshotSource.Failed(32, Now.AddHours(-5), Now.AddMinutes(-2));
        var result = (await h.Receivables.RequestLoadAsync(h.Manager, From, To, default, 32)).Value!;
        Assert.True(result.Accepted);
        Assert.Equal([32], h.Loader.CompanyRequests);
        Assert.Empty(h.Loader.Requests);                                                   // not a range load of both companies

        // A fresh company is retried too (the user asked); only a running load stops it.
        var fresh = new Harness();
        Assert.True((await fresh.Receivables.RequestLoadAsync(fresh.Manager, From, To, default, 4)).Value!.Accepted);
        var running = new Harness();
        running.Source.Companies[32] = FakeSnapshotSource.Healthy(32, Now.AddHours(-5)) with { RefreshInProgress = true };
        Assert.True((await running.Receivables.RequestLoadAsync(running.Manager, From, To, default, 32)).Value!.AlreadyRunning);
        Assert.Empty(running.Loader.CompanyRequests);
    }

    [Fact]
    public async Task Retry_RefusesAnUnknownCompany_AndReportingUsers()
    {
        var h = new Harness();
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Receivables.RequestLoadAsync(h.Manager, From, To, default, 7)).Outcome);
        Assert.Equal(CollectionsOutcome.Forbidden, (await h.Receivables.RequestLoadAsync(new CollectionsCaller(Guid.NewGuid(), [Roles.ReportingUser], []), From, To, default, 32)).Outcome);
        Assert.Empty(h.Loader.CompanyRequests);
    }
}

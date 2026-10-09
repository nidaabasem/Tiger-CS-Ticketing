using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Tests.Collections.Snapshot;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// The Campaigns preview/export, the review refresh and the dispatch revalidation read the same local snapshot with the same filters (configured start, minimum
/// outstanding amount) and must agree on amounts and eligibility; the dispatch keeps its approval and freshness checks. Nothing here reaches Genesys: the client is the
/// scripted <see cref="FakeGenesysClient"/> and all contacts are synthetic.
/// </summary>
public sealed class CampaignReviewDispatchConsistencyTests
{
    private static SnapshotStatusDto Status(DateTime now, DateTime successUtc, bool covered = true)
    {
        var status = ReceivablesSnapshotComposer.BuildStatus(new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 31),
            new[] { FakeSnapshotSource.Healthy(4, successUtc), FakeSnapshotSource.Healthy(32, successUtc) }, new List<UnmatchedTowerDto>(), now, 90);
        return covered ? status : status with { CoverageGaps = [new CoverageGapDto(4, "Company 4", new DateOnly(2000, 1, 1), new DateOnly(2025, 12, 31))] };
    }

    private static ReviewHarness Harness()
    {
        var h = new ReviewHarness();
        h.Source.HonourMinAmount = true;
        h.Source.Snapshot = Status(ReviewHarness.Now, ReviewHarness.Now.AddMinutes(-5));
        h.Source.Items.AddRange([ReviewHarness.Row(1, 99.99m), ReviewHarness.Row(2, 100m), ReviewHarness.Row(3, 250.5m), ReviewHarness.Row(4, 0m)]);
        return h;
    }

    private static CollectionsCampaignAppService Preview(ReviewHarness h) =>
        new(h.Options, h.Campaign, h.Sql, new(h.Options, new TigerCS.Tests.IdentityAndAccess.Fakes.FakeDepartmentRepository()),
            new(h.Options, h.Time), h.Source, Microsoft.Extensions.Logging.Abstractions.NullLogger<CollectionsCampaignAppService>.Instance);

    [Fact]
    public async Task PreviewReviewExportAndDispatch_UseTheSameMinimumStartAndAmounts()
    {
        using var h = Harness();
        var date = new DateOnly(2026, 10, 14);
        var preview = (await Preview(h).PreviewAsync(h.Manager, "CurrentMonthReminder", date)).Value!;
        var run = await h.RefreshAsync();
        Assert.Equal("Completed", run.Status);
        using var scope = h.NewScope();
        var review = (await scope.Query.QueryAsync(h.Manager, new ReviewFilter(ReminderType: "CurrentMonth", PaymentStatus: "All"), 1, 100, CancellationToken.None)).Value!;

        // Same units, same amounts: the 99.99 instalment is below the minimum (100, inclusive) for every consumer; the settled row is nobody's candidate.
        Assert.Equal(["T0002", "T0003"], preview.Items.Select(i => i.TenantId).Order());
        Assert.Equal(preview.Items.OrderBy(i => i.UnitCode).Select(i => (i.UnitCode, i.Amount)),
                     review.Items.OrderBy(i => i.UnitCode).Select(i => (i.UnitCode, i.RemainingAmount)));
        var export = (await Preview(h).ExportAsync(h.Manager, "CurrentMonthReminder", "review", date)).Value!;
        Assert.Equal(preview.TotalCount, export.RowCount);

        // Every source read of the three flows carried the same filters.
        Assert.All(h.Source.Requests, r => Assert.Equal(100m, r.MinAmount));
        Assert.All(h.Source.Requests, r => Assert.Equal(DateOnly.FromDateTime(h.Sql.StartDate), r.FromDate));

        // Dispatch: approval of the exact list, revalidation against the same snapshot, upload of exactly those contacts (to the scripted client).
        var (summary, confirm) = await h.PrepareAsync();
        Assert.Equal(2, summary.Count);
        using (var s = h.NewScope()) Assert.Equal(CollectionsOutcome.Accepted, (await s.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        using (var s = h.NewScope()) await s.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Last(), CancellationToken.None);
        var sent = h.Client.Calls.SelectMany(c => c.Contacts).ToList();
        Assert.Equal(2, sent.Count);
        Assert.Equal(preview.Items.Sum(i => i.Amount), sent.Sum(c => decimal.Parse(c.AmountDue, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.All(h.Source.Requests, r => Assert.Equal(100m, r.MinAmount));
    }

    [Fact]
    public async Task Dispatch_KeepsItsApprovalCheck_AndRefusesToRevalidateAgainstAStaleSnapshot()
    {
        using var h = Harness();
        await h.RefreshAsync();
        var (_, confirm) = await h.PrepareAsync();
        using (var s = h.NewScope()) Assert.Equal(CollectionsOutcome.Accepted, (await s.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        // A different count/fingerprint is not an approval of this list.
        using (var s = h.NewScope())
            Assert.NotEqual(CollectionsOutcome.Accepted, (await s.Dispatch.ConfirmAsync(h.Manager, confirm with { ExpectedCount = confirm.ExpectedCount + 1, IdempotencyKey = "k2" }, CancellationToken.None)).Outcome);

        // The local snapshot is older than the maximum age when the job runs: it must not be read as "no balance left", and nothing is sent.
        h.Source.Snapshot = Status(ReviewHarness.Now, ReviewHarness.Now.AddHours(-10));
        DispatchDto result;
        using (var s = h.NewScope())
        {
            await s.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Last(), CancellationToken.None);
            var id = (await s.Dispatch.ListAsync(h.Manager, CancellationToken.None)).Value![0].DispatchId;
            result = (await s.Dispatch.GetAsync(h.Manager, id, CancellationToken.None)).Value!;
        }
        Assert.Empty(h.Client.Calls);
        Assert.Equal("Failed", result.Status);
        Assert.Equal(0, result.UploadedCount);
    }

    [Fact]
    public async Task ReviewRefresh_RefusesASnapshotThatDoesNotCoverTheWindow_InsteadOfOmittingReceivables()
    {
        using var h = Harness();
        h.Source.Snapshot = Status(ReviewHarness.Now, ReviewHarness.Now.AddMinutes(-5), covered: false);
        var run = await h.RefreshAsync();
        Assert.Equal("Failed", run.Status);
        Assert.Contains("not ready", run.Error);
        Assert.Equal(0, run.RecordCount);
    }
}

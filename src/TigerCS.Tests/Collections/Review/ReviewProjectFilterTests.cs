using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Tests.IdentityAndAccess.Fakes;

namespace TigerCS.Tests.Collections.Review;

/// <summary>The structured Project / Tower filter is the same on Campaigns and Review, survives approval and dispatch, and is never silently widened.</summary>
public sealed class ReviewProjectFilterTests
{
    private static PactReceivableInstalment InTower(PactReceivableInstalment row, int tower) =>
        row with { TowerId = tower, TowerNumber = tower == 7 ? "140" : "141", TowerName = tower == 7 ? "Al Ghaf" : "Al Sidr" };

    // Clock = 2026-10-14. Tower 7: units 1, 2 (October, Due) and unit 4 (September 1,600 -> Legal Notice, Overdue). Tower 8: unit 3 (October).
    private static async Task<ReviewHarness> SeededAsync(bool withTowers = true, int day = 20)
    {
        var h = new ReviewHarness(false);
        PactReceivableInstalment T(PactReceivableInstalment r, int tower) => withTowers ? InTower(r, tower) : r;
        h.Source.Items.Add(T(ReviewHarness.Row(1, 200m, day: day), 7));
        h.Source.Items.Add(T(ReviewHarness.Row(2, 300m, day: day), 7));
        h.Source.Items.Add(T(ReviewHarness.Row(3, 400m, day: day), 8));
        h.Source.Items.Add(T(ReviewHarness.Row(4, 1600m) with { DueDate = new DateTime(2026, 9, 10) }, 7));
        await h.RefreshAsync();
        return h;
    }

    private static async Task<ReviewPageDto> PageAsync(ReviewHarness h, ReviewFilter filter)
    {
        using var scope = h.NewScope();
        var page = await scope.Query.QueryAsync(h.Manager, filter with { MinRemaining = 0m }, 1, 100, CancellationToken.None);
        Assert.True(page.IsSuccess, page.Detail);
        return page.Value!;
    }

    [Fact]
    public async Task TheTowerFilter_ListsOnlyThatTowersRecords_AndEveryRecordNamesItsTower()
    {
        using var h = await SeededAsync();
        var all = await PageAsync(h, new ReviewFilter());
        var tower7 = await PageAsync(h, new ReviewFilter(TowerId: 7));
        var tower8 = await PageAsync(h, new ReviewFilter(TowerId: 8));

        Assert.True(all.Items.Count > tower7.Items.Count);
        Assert.All(tower7.Items, r => Assert.Equal(("140", "Al Ghaf"), (r.TowerNumber, r.TowerName)));
        Assert.All(tower8.Items, r => Assert.Equal("TP140-3", r.UnitCode));
        Assert.Equal(all.Items.Select(r => r.RecordKey).Order(), tower7.Items.Concat(tower8.Items).Select(r => r.RecordKey).Order());   // the towers partition "All projects"
    }

    [Fact]
    public async Task StageProjectStatusAndDates_Combine_OnReview()
    {
        using var h = await SeededAsync();
        var page = await PageAsync(h, new ReviewFilter(TowerId: 7, ReminderType: "LegalNotice", DueStatus: "Overdue", DueFrom: new(2026, 9, 1), DueTo: new(2026, 9, 30)));
        Assert.All(page.Items, r => Assert.Equal(("LegalNotice", "TP140-4"), (r.ReminderType, r.UnitCode)));
        Assert.NotEmpty(page.Items);
        // Same stage and tower but the Due status: nothing (the legal-notice balance is last month's).
        Assert.Empty((await PageAsync(h, new ReviewFilter(TowerId: 7, ReminderType: "LegalNotice", DueStatus: "Due"))).Items);
        // Another tower: nothing, and it is not widened to all projects.
        Assert.Empty((await PageAsync(h, new ReviewFilter(TowerId: 8, ReminderType: "LegalNotice", DueStatus: "Overdue"))).Items);
    }

    [Fact]
    public async Task DataReadBeforeTowersWereStored_SaysSo_InsteadOfLookingEmptyOrBroadening()
    {
        using var h = await SeededAsync(withTowers: false);
        var tower7 = await PageAsync(h, new ReviewFilter(TowerId: 7));
        Assert.Empty(tower7.Items);
        Assert.True(tower7.TowerDataMissing);
        Assert.False((await PageAsync(h, new ReviewFilter())).TowerDataMissing);
    }

    [Fact]
    public async Task AnInvalidTower_IsRefused()
    {
        using var h = await SeededAsync();
        using var scope = h.NewScope();
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await scope.Query.QueryAsync(h.Manager, new ReviewFilter(TowerId: 0), 1, 25, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task ApprovalAndDispatch_KeepTheExactFilterSnapshot_AndUploadOnlyThatProject()
    {
        using var h = await SeededAsync();
        var filter = new ReviewFilter(TowerId: 7, ReminderType: "CurrentMonth", DueStatus: "Due", DueFrom: new(2026, 10, 1), DueTo: new(2026, 10, 31), MinRemaining: 0m);
        var (summary, confirm) = await h.PrepareAsync(h.All(filter), "tower-1");
        Assert.Equal(2, summary.Count);

        using (var scope = h.NewScope())
        {
            Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
            await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Last(), CancellationToken.None);
        }

        using var context = h.CreateContext();
        var dispatch = await context.CollectionsDispatches.AsNoTracking().Include(d => d.Items).SingleAsync();
        var stored = System.Text.Json.JsonSerializer.Deserialize<ReviewFilter>(dispatch.FilterJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal((7, "CurrentMonth", "Due", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)), (stored.TowerId, stored.ReminderType, stored.DueStatus, stored.DueFrom, stored.DueTo));
        Assert.Equal(["TP140-1", "TP140-2"], dispatch.Items.Select(i => i.UnitCode).Order());       // not tower 8's unit 3
        Assert.Equal(summary.Count, dispatch.ApprovedCount);
        Assert.DoesNotContain(h.Client.Calls.SelectMany(c => c.Contacts), c => c.CustomerName == "Customer 3");
    }

    [Fact]
    public async Task ChangingTheProjectAfterThePreview_CannotSlipPastApproval()
    {
        using var h = await SeededAsync();
        var (_, confirm) = await h.PrepareAsync(h.All(new ReviewFilter(TowerId: 7, ReminderType: "CurrentMonth", MinRemaining: 0m)), "tower-2");
        var widened = confirm with { Selection = h.All(new ReviewFilter(ReminderType: "CurrentMonth", MinRemaining: 0m)) };   // All projects
        using var scope = h.NewScope();
        var result = await scope.Dispatch.ConfirmAsync(h.Manager, widened, CancellationToken.None);
        Assert.False(result.IsSuccess);                                                  // 3 customers now match, 2 were approved
        Assert.Equal(CollectionsOutcome.ReviewRequired, result.Outcome);
    }

    [Fact]
    public async Task CampaignsAndReview_AgreeOnUnitsAndAmounts_ForTheSameStageProjectAndStatus()
    {
        using var h = await SeededAsync(day: 10);          // instalments already due (today = 14 October)
        var campaigns = new CollectionsCampaignAppService(h.Options, h.Campaign, h.Sql, new(h.Options, new FakeDepartmentRepository()),
            new(h.Options, h.Time), h.Source, NullLogger<CollectionsCampaignAppService>.Instance);
        foreach (var (tower, status) in new[] { (7, "Due"), (7, "Both"), (8, "Due") })
        {
            var preview = (await campaigns.PreviewAsync(h.Manager, "CurrentMonthReminder", towerId: tower, minTotal: 0m, paymentStatus: status)).Value!;
            var eligible = preview.Items.Where(i => i.Status != "NotEligible").ToList();
            var review = await PageAsync(h, new ReviewFilter(TowerId: tower, ReminderType: "CurrentMonth", DueStatus: status == "Both" ? null : status, PaymentStatus: "All"));

            Assert.Equal(eligible.Select(i => i.UnitCode).Order(), review.Items.Select(r => r.UnitCode).Order());
            Assert.Equal(eligible.Sum(i => i.Amount ?? 0m), review.Items.Sum(r => r.RemainingAmount ?? 0m));          // the campaign's qualifying amount is the review amount
            Assert.Equal(preview.EligibleTotalAmount, review.Items.Sum(r => r.RemainingAmount ?? 0m));
        }
    }

    /// <summary>
    /// An instalment due later in the CURRENT month (20 October when today is 14 October) was the one pinned difference between Campaigns and Review. Both now apply the stage window
    /// (the whole month for Current month / Follow up) through the same <c>CollectionsCampaignPolicy</c> call, so it is eligible in both. The broader matrix is in
    /// <c>CampaignReviewEligibilityParityTests</c>.
    /// </summary>
    [Fact]
    public async Task AnInstalmentDueLaterThisMonth_IsEligibleInBothCampaignsAndReview()
    {
        using var h = await SeededAsync(day: 20);
        var campaigns = new CollectionsCampaignAppService(h.Options, h.Campaign, h.Sql, new(h.Options, new FakeDepartmentRepository()),
            new(h.Options, h.Time), h.Source, NullLogger<CollectionsCampaignAppService>.Instance);
        var preview = (await campaigns.PreviewAsync(h.Manager, "CurrentMonthReminder", towerId: 7, minTotal: 0m, paymentStatus: "Due")).Value!;
        Assert.Equal(2, preview.TotalCount);
        Assert.All(preview.Items, i => Assert.NotEqual("NotEligible", i.Status));
        Assert.Equal(500m, preview.EligibleTotalAmount);                                                      // 200 + 300
        var review = await PageAsync(h, new ReviewFilter(TowerId: 7, ReminderType: "CurrentMonth", PaymentStatus: "All"));
        Assert.Equal(preview.Items.Select(i => i.UnitCode).Order(), review.Items.Select(r => r.UnitCode).Order());
        Assert.Equal(preview.EligibleTotalAmount, review.Items.Sum(r => r.RemainingAmount ?? 0m));
    }
}

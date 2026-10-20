using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;

namespace TigerCS.Tests.Collections.Review;

/// <summary>The Due / Overdue / Both filter on Review &amp; approve: it narrows the list, and approval + dispatch re-resolve the very same filter.</summary>
public sealed class ReviewDueStatusTests
{
    // Clock = 2026-10-14. Two October rows (Due) and two September rows (Overdue).
    private static async Task<ReviewHarness> SeededAsync()
    {
        var h = new ReviewHarness(false);
        h.Source.Items.Add(ReviewHarness.Row(1, 200m));
        h.Source.Items.Add(ReviewHarness.Row(2, 300m));
        h.Source.Items.Add(ReviewHarness.Row(3, 1600m) with { DueDate = new DateTime(2026, 9, 10) });
        h.Source.Items.Add(ReviewHarness.Row(4, 1700m) with { DueDate = new DateTime(2026, 9, 11) });
        await h.RefreshAsync();
        return h;
    }

    private static async Task<List<ReviewRecordDto>> RecordsAsync(ReviewHarness h, string? dueStatus)
    {
        using var scope = h.NewScope();
        var page = await scope.Query.QueryAsync(h.Manager, new ReviewFilter(DueStatus: dueStatus, MinRemaining: 0m), 1, 100, CancellationToken.None);
        Assert.True(page.IsSuccess, page.Detail);
        return page.Value!.Items.ToList();
    }

    [Fact]
    public async Task Due_ListsOnlyTheCurrentMonth_Overdue_OnlyBefore_Both_Everything()
    {
        using var h = await SeededAsync();
        var both = await RecordsAsync(h, null);
        var due = await RecordsAsync(h, "Due");
        var overdue = await RecordsAsync(h, "Overdue");

        Assert.NotEmpty(due); Assert.NotEmpty(overdue);
        Assert.All(due, r => Assert.True(r.DueDate >= new DateOnly(2026, 10, 1)));
        Assert.All(overdue, r => Assert.True(r.DueDate < new DateOnly(2026, 10, 1)));
        Assert.Equal(both.Select(r => r.RecordKey).Order(), due.Concat(overdue).Select(r => r.RecordKey).Order());   // together they are exactly "Both"
        Assert.Equal(both.Count, (await RecordsAsync(h, "Both")).Count);
    }

    [Fact]
    public async Task DueStatus_IsCombinedWithTheDateRange_NeverWidensIt()
    {
        using var h = await SeededAsync();
        using var scope = h.NewScope();
        // Overdue + a range reaching into October: still only the days before October.
        var page = (await scope.Query.QueryAsync(h.Manager, new ReviewFilter(DueStatus: "Overdue", DueFrom: new(2026, 9, 11), DueTo: new(2026, 10, 31), MinRemaining: 0m), 1, 100, CancellationToken.None)).Value!;
        Assert.All(page.Items, r => Assert.InRange(r.DueDate!.Value, new DateOnly(2026, 9, 11), new DateOnly(2026, 9, 30)));
        // Due + a range that ends in September: nothing, not an error.
        var none = await scope.Query.QueryAsync(h.Manager, new ReviewFilter(DueStatus: "Due", DueTo: new(2026, 9, 30), MinRemaining: 0m), 1, 100, CancellationToken.None);
        Assert.True(none.IsSuccess); Assert.Empty(none.Value!.Items);
    }

    [Fact]
    public async Task AnUnknownDueStatus_IsRefused()
    {
        using var h = await SeededAsync();
        using var scope = h.NewScope();
        var result = await scope.Query.QueryAsync(h.Manager, new ReviewFilter(DueStatus: "Late"), 1, 25, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.InvalidRequest, result.Outcome);
    }

    [Fact]
    public async Task ApprovalAndDispatch_UseTheSameFilterSnapshot_IncludingTheDueStatus()
    {
        using var h = await SeededAsync();
        var selection = h.All(new ReviewFilter(DueStatus: "Due", ReminderType: "CurrentMonth", MinRemaining: 0m));
        var (summary, confirm) = await h.PrepareAsync(selection, "due-1");
        Assert.True(summary.Count > 0);

        using (var scope = h.NewScope())
        {
            var accepted = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
            Assert.Equal(CollectionsOutcome.Accepted, accepted.Outcome);
            await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Last(), CancellationToken.None);
        }

        using var context = h.CreateContext();
        var dispatch = await context.CollectionsDispatches.AsNoTracking().Include(d => d.Items).SingleAsync();
        Assert.Contains("\"DueStatus\":\"Due\"", dispatch.FilterJson);                       // the approved filter is stored with the send
        Assert.Equal(summary.Count, dispatch.ApprovedCount);
        Assert.All(dispatch.Items, i => Assert.True(i.DueDate >= new DateOnly(2026, 10, 1)));  // nothing outside the approved status was frozen
        Assert.All(h.Client.Calls.SelectMany(c => c.Contacts), c => Assert.StartsWith("2026-10", c.DueDate));
    }

    [Fact]
    public async Task AFilterChangedAfterThePreview_IsRefused_NotSilentlyReplaced()
    {
        using var h = await SeededAsync();
        var (_, confirm) = await h.PrepareAsync(h.All(new ReviewFilter(DueStatus: "Due", ReminderType: "CurrentMonth", MinRemaining: 0m)), "due-2");
        var widened = confirm with { Selection = h.All(new ReviewFilter(ReminderType: "CurrentMonth", MinRemaining: 0m)) };   // Both instead of Due
        using var scope = h.NewScope();
        var result = await scope.Dispatch.ConfirmAsync(h.Manager, widened, CancellationToken.None);
        if (result.IsSuccess) Assert.Equal(confirm.ExpectedCount, result.Value!.ApprovedCount);   // identical result set is fine
        else Assert.Equal(CollectionsOutcome.ReviewRequired, result.Outcome);
    }
}

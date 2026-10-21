using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Tests.Collections.Services;

namespace TigerCS.Tests.Collections.Review;

/// <summary>A refresh must always end Completed or Failed with a reason, never wait in Queued/Running forever.</summary>
public sealed class ReviewRefreshLifecycleTests
{
    [Fact]
    public async Task JobsDisabled_FailsImmediatelyWithReason_AndANewRefreshCanBeStarted()
    {
        using var h = new ReviewHarness();
        h.Scheduler.Available = false;
        using var scope = h.NewScope();
        var started = await scope.Refresh.StartAsync(h.Manager, null, CancellationToken.None);
        Assert.True(started.IsSuccess);
        Assert.Equal("Failed", started.Value!.Status);
        Assert.Contains("BackgroundJobs:Enabled", started.Value.Error);
        Assert.Empty(h.Scheduler.Refreshes);

        h.Scheduler.Available = true;
        var retry = await scope.Refresh.StartAsync(h.Manager, null, CancellationToken.None);
        Assert.Equal("Queued", retry.Value!.Status);
    }

    [Fact]
    public async Task QueueFailure_IsPersistedAsFailed()
    {
        using var h = new ReviewHarness();
        h.Scheduler.EnqueueFailure = new InvalidOperationException("storage down");
        using var scope = h.NewScope();
        var started = await scope.Refresh.StartAsync(h.Manager, null, CancellationToken.None);
        Assert.Equal("Failed", started.Value!.Status);
        Assert.Contains("storage down", started.Value.Error);
    }

    [Fact]
    public async Task QueuedRunNobodyPicksUp_IsMarkedFailedByTheProgressRead()
    {
        using var h = new ReviewHarness();
        using var scope = h.NewScope();
        var started = await scope.Refresh.StartAsync(h.Manager, null, CancellationToken.None);
        Assert.Equal("Queued", started.Value!.Status);
        Assert.Equal("Queued", (await scope.Query.GetCurrentRunAsync(h.Manager, CancellationToken.None)).Value!.Status);

        h.Time.Advance(TimeSpan.FromMinutes(h.Review.QueuedTimeoutMinutes + 1));
        var run = (await scope.Query.GetCurrentRunAsync(h.Manager, CancellationToken.None)).Value!;
        Assert.Equal("Failed", run.Status);
        Assert.Contains("not picked up", run.Error);
    }

    [Fact]
    public async Task UnexpectedWorkerException_IsPersistedAndRunIsNotLeftRunning()
    {
        using var h = new ReviewHarness();
        h.Source.Failure = new InvalidOperationException("boom");
        using var scope = h.NewScope();
        var started = await scope.Refresh.StartAsync(h.Manager, null, CancellationToken.None);
        await scope.Refresh.RunAsync(started.Value!.RunId, CancellationToken.None);
        var run = (await scope.Query.GetRunAsync(h.Manager, started.Value.RunId, CancellationToken.None)).Value!;
        Assert.Equal("Failed", run.Status);
        Assert.Contains("boom", run.Error);
    }
}

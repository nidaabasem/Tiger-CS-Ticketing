using Hangfire;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Review;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>The explicit "refresh review data" job. No automatic retry: a failed run is reported and a person starts a new one.</summary>
public sealed class CollectionsReviewRefreshJob(ReviewRefreshService refresh)
{
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(long runId, CancellationToken cancellationToken) => refresh.RunAsync(runId, cancellationToken);
}

/// <summary>
/// Revalidates and uploads one approved dispatch. Safe to execute twice: the dispatch lease admits one worker, batches are claimed
/// atomically, and a batch whose outcome is unknown is never resent.
/// </summary>
public sealed class CollectionsDispatchJob(DispatchService dispatch)
{
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 300, 900])]
    public Task RunAsync(long dispatchId, CancellationToken cancellationToken) => dispatch.ExecuteAsync(dispatchId, cancellationToken);
}

/// <summary>
/// Marks uploaded contacts not callable when their balance was paid or changed. Safe to overlap or repeat: each update is an idempotent PUT and
/// an in-process guard allows one sweep at a time.
/// </summary>
public sealed class CollectionsSuppressionJob(SuppressionService suppression, ILogger<CollectionsSuppressionJob> logger)
{
    public const string RecurringJobId = "collections-genesys-suppression";

    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var result = await suppression.SweepAsync(cancellationToken);
        if (!result.Ran) logger.LogInformation("Suppression sweep skipped: {Detail}", result.Detail);
    }
}

public sealed class HangfireReviewJobScheduler(IBackgroundJobClient client) : IReviewJobScheduler
{
    public void EnqueueRefresh(long runId) => client.Enqueue<CollectionsReviewRefreshJob>(j => j.RunAsync(runId, CancellationToken.None));

    public void EnqueueDispatch(long dispatchId) => client.Enqueue<CollectionsDispatchJob>(j => j.RunAsync(dispatchId, CancellationToken.None));

    public void EnqueueSuppressionSweep() => client.Enqueue<CollectionsSuppressionJob>(j => j.RunAsync(CancellationToken.None));
}

/// <summary>Used when Hangfire is switched off: work stays Queued and is reported as such. Nothing runs silently.</summary>
public sealed class NoOpReviewJobScheduler(ILogger<NoOpReviewJobScheduler> logger) : IReviewJobScheduler
{
    public void EnqueueRefresh(long runId) => logger.LogWarning("Background jobs are disabled; review refresh {RunId} stays queued.", runId);

    public void EnqueueDispatch(long dispatchId) => logger.LogWarning("Background jobs are disabled; dispatch {DispatchId} stays queued.", dispatchId);

    public void EnqueueSuppressionSweep() => logger.LogWarning("Background jobs are disabled; the suppression sweep did not run.");
}

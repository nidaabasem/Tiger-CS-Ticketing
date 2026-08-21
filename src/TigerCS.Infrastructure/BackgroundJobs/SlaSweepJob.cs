using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.SlaAndEscalation.Services;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// SLA-Architecture.md §14's safety sweep — a recurring job that re-scans due
/// deadlines <b>solely</b> to catch a scheduled job lost to a deploy or
/// restart. It is never the primary detection path, and on a healthy system
/// it records nothing.
/// </summary>
public sealed class SlaSweepJob(SlaBreachDetectionAppService breachDetection, ILogger<SlaSweepJob> logger)
{
    /// <summary>The Hangfire recurring-job identifier, named once so registration and any later reconfiguration cannot drift apart.</summary>
    public const string RecurringJobId = "sla-breach-safety-sweep";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var result = await breachDetection.SweepAsync(cancellationToken);

        if (result.BatchLimitReached)
        {
            // Surfaced, never silent: a full batch means candidates were left
            // for the next tick, and an operator reading only the recorded
            // count would otherwise read a truncated pass as a caught-up one.
            logger.LogWarning(
                "SLA safety sweep hit its batch limit of {BatchSize}; more due deadlines remain and will be picked up on the next tick.",
                SlaBreachDetectionAppService.SweepBatchSize);
        }

        if (result.BreachesRecorded > 0)
        {
            // The sweep is a backstop. Anything it finds means a scheduled
            // job did not fire, which is worth an operator's attention even
            // though the breach itself was caught correctly.
            logger.LogWarning(
                "SLA safety sweep recorded {BreachesRecorded} breach(es) out of {DeadlinesExamined} due deadline(s) — "
                + "these should have been caught by their scheduled jobs (ADR-0015).",
                result.BreachesRecorded, result.DeadlinesExamined);
            return;
        }

        logger.LogDebug(
            "SLA safety sweep examined {DeadlinesExamined} due deadline(s) and recorded no new breaches.",
            result.DeadlinesExamined);
    }
}

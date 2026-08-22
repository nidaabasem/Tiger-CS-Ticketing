using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Notifications.Services;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// ADR-0015's "recurring job for the Outbox dispatcher", and the only thing
/// in this system that turns a committed Outbox row into an actual outbound
/// effect (ADR-0013 / NFR-REL-01).
///
/// <para>
/// Thin by design: Hangfire's job is scheduling and persistence, so this type
/// does nothing but invoke <see cref="OutboxDispatcher"/> and report what
/// happened. All reliability behaviour — claiming, backoff, dead-lettering —
/// lives in the dispatcher, where it is testable without a scheduler.
/// </para>
/// </summary>
public sealed class OutboxDispatchJob(OutboxDispatcher dispatcher, ILogger<OutboxDispatchJob> logger)
{
    /// <summary>The Hangfire recurring-job identifier, named once so registration and any later reconfiguration cannot drift apart.</summary>
    public const string RecurringJobId = "outbox-dispatch";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var result = await dispatcher.DispatchPendingAsync(cancellationToken);

        if (result.Total == 0)
        {
            logger.LogDebug("Outbox dispatcher found no messages eligible for dispatch.");
            return;
        }

        if (result.DeadLettered > 0 || result.Failed > 0)
        {
            // ADR-0020 alerts on the dead-letter count; a warning here is what
            // makes that count visible without a dashboard. Counts only — no
            // identifiers, addresses or payloads reach the log.
            logger.LogWarning(
                "Outbox dispatch pass: {Processed} processed, {Retried} retried, {DeadLettered} dead-lettered, "
                + "{Failed} outcome-write failures, {Skipped} claimed by another worker.",
                result.Processed, result.Retried, result.DeadLettered, result.Failed, result.Skipped);
            return;
        }

        logger.LogInformation(
            "Outbox dispatch pass: {Processed} processed, {Retried} retried, {Skipped} claimed by another worker.",
            result.Processed, result.Retried, result.Skipped);
    }
}

using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// The unit of work one scheduled deadline job performs (SLA-Architecture.md
/// §13). A thin wrapper: all the decision-making lives in
/// <see cref="SlaBreachDetectionAppService"/>, so breach correctness is
/// testable with no scheduler present at all.
///
/// <para>
/// Safe to retry. Hangfire's default retry behaviour must be aligned with
/// ADR-0014's idempotency requirement — "naive retries alone do not guarantee
/// no duplicate effect" — and it is, because every attempt targets the same
/// <c>TicketId + CheckType + DueTimestamp</c> key and the first one to commit
/// wins.
/// </para>
/// </summary>
public sealed class SlaDeadlineCheckJob(
    SlaBreachDetectionAppService breachDetection,
    ILogger<SlaDeadlineCheckJob> logger)
{
    public async Task RunAsync(long ticketId, SlaDeadlineType deadlineType, CancellationToken cancellationToken)
    {
        var outcome = await breachDetection.CheckDeadlineAsync(ticketId, deadlineType, cancellationToken);

        if (outcome == SlaBreachProcessingOutcome.BreachRecorded)
        {
            logger.LogWarning(
                "SLA {DeadlineType} breach recorded for ticket {TicketId} by the scheduled deadline job.",
                deadlineType, ticketId);
            return;
        }

        logger.LogDebug(
            "Scheduled {DeadlineType} deadline check for ticket {TicketId} completed with outcome {Outcome}.",
            deadlineType, ticketId, outcome);
    }
}

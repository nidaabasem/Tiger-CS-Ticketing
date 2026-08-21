using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Application.Modules.SlaAndEscalation.Services;

/// <summary>
/// The entry point both background-job paths of ADR-0015 call (backlog S-10).
///
/// <list type="bullet">
///   <item><description>
///     <see cref="CheckDeadlineAsync"/> — SLA-Architecture.md §13's
///     <b>primary</b> mechanism: one scheduled (delayed) job per due
///     timestamp, enqueued when the timestamp is computed and firing at the
///     due moment.
///   </description></item>
///   <item><description>
///     <see cref="SweepAsync"/> — §14's <b>backstop only</b>: a recurring
///     re-scan that exists solely to catch a scheduled job lost to a deploy
///     or restart. It is never the primary detection path.
///   </description></item>
/// </list>
///
/// <para>
/// <b>Neither path depends on a browser, a SignalR client, or any connected
/// user.</b> Detection is server-side background work against stored due
/// timestamps (FR-SLA-04: the columns "exist and are queryable independent of
/// any background job"; ADR-0016 restricts SignalR to state-change
/// notification and explicitly not to countdown or detection).
/// </para>
///
/// <para>
/// <b>Each ticket is its own transaction.</b> A sweep covering many tickets
/// must not lose the work already done for earlier ones because a later one
/// failed, and a per-ticket commit keeps the idempotency claim and the
/// effects it guards atomic.
/// </para>
/// </summary>
public sealed class SlaBreachDetectionAppService(
    ITicketRepository ticketRepository,
    ITicketSlaInstanceRepository slaInstanceRepository,
    SlaBreachProcessor breachProcessor,
    ITicketingUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>
    /// How many candidate tickets one sweep pass handles per deadline type.
    /// A bound rather than an unbounded scan so a backlog cannot turn one
    /// sweep tick into an open-ended transaction storm; anything left over is
    /// picked up by the next tick, since nothing is marked as consumed.
    /// </summary>
    public const int SweepBatchSize = 200;

    /// <summary>SLA-Architecture.md §13 — the check a scheduled deadline job performs for one ticket and one deadline.</summary>
    public async Task<SlaBreachProcessingOutcome> CheckDeadlineAsync(
        long ticketId, SlaDeadlineType deadlineType, CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return SlaBreachProcessingOutcome.NoSlaPeriod;
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var correlationId = Guid.NewGuid();

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var outcome = await breachProcessor.ProcessDeadlineAsync(ticket, deadlineType, nowUtc, correlationId, cancellationToken);

        if (outcome != SlaBreachProcessingOutcome.BreachRecorded)
        {
            // Nothing was written. Rolling back explicitly keeps the
            // "no side effects on a repeat" guarantee visible at the call
            // site rather than resting on there being nothing to save.
            await transaction.RollbackAsync(cancellationToken);
            return outcome;
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateWriteException)
        {
            // The unique index on IdempotencyRecords.IdempotencyKey (or the
            // filtered one on TicketEscalations) rejected the write because
            // a concurrent scheduled job and sweep both got past the
            // read-side check for the same due event. That is precisely the
            // race those indexes exist to lose safely: the other transaction
            // recorded the breach, this one records nothing.
            await transaction.RollbackAsync(cancellationToken);
            return SlaBreachProcessingOutcome.AlreadyProcessed;
        }
        catch (TicketConcurrentlyModifiedException)
        {
            // The ticket changed under us mid-check. The sweep will re-read
            // it on the next tick and, because the idempotency key was never
            // committed, will evaluate the same due event again.
            await transaction.RollbackAsync(cancellationToken);
            return SlaBreachProcessingOutcome.AlreadyProcessed;
        }

        await transaction.CommitAsync(cancellationToken);
        return SlaBreachProcessingOutcome.BreachRecorded;
    }

    /// <summary>
    /// SLA-Architecture.md §14's safety sweep. Re-scans current SLA periods
    /// whose deadlines have fallen due without being flagged, and runs the
    /// same check the scheduled job would have.
    /// </summary>
    /// <returns>
    /// What the pass did. <c>BreachesRecorded</c> is zero on a healthy system
    /// — the sweep is a backstop, so finding nothing is the expected result.
    /// </returns>
    public async Task<SlaSweepResult> SweepAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var recorded = 0;
        var examined = 0;
        var batchLimitReached = false;

        foreach (var deadlineType in new[] { SlaDeadlineType.FirstResponse, SlaDeadlineType.Resolution })
        {
            var ticketIds = await slaInstanceRepository.ListTicketIdsWithDeadlineDueAsync(
                deadlineType, nowUtc, SweepBatchSize, cancellationToken);

            // Never silently truncate: a full batch means more candidates are
            // waiting, and an operator reading only the recorded count would
            // otherwise believe the sweep had caught up. The caller surfaces
            // this — see HangfireSlaSweepJob.
            batchLimitReached |= ticketIds.Count >= SweepBatchSize;
            examined += ticketIds.Count;

            foreach (var ticketId in ticketIds)
            {
                var outcome = await CheckDeadlineAsync(ticketId, deadlineType, cancellationToken);
                if (outcome == SlaBreachProcessingOutcome.BreachRecorded)
                {
                    recorded++;
                }
            }
        }

        return new SlaSweepResult(examined, recorded, batchLimitReached);
    }
}

/// <summary>The outcome of one safety-sweep pass (SLA-Architecture.md §14).</summary>
/// <param name="DeadlinesExamined">How many due-but-unflagged deadlines the pass looked at, across both clocks.</param>
/// <param name="BreachesRecorded">How many of them this pass newly recorded as breached. Zero is the healthy result — the scheduled jobs should have caught them first.</param>
/// <param name="BatchLimitReached">True when a batch came back full, meaning more candidates remain for the next tick. Surfaced so a backlog is never mistaken for an idle sweep.</param>
public sealed record SlaSweepResult(int DeadlinesExamined, int BreachesRecorded, bool BatchLimitReached);

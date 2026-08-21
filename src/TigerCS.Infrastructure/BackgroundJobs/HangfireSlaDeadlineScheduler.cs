using Hangfire;
using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;
using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// SLA-Architecture.md §13 / ADR-0015 — the primary breach-detection
/// mechanism: one Hangfire scheduled (delayed) job per due timestamp,
/// enqueued the moment that timestamp is computed, firing at the due moment
/// itself rather than on a poll.
///
/// <para>
/// Hangfire's storage is the same SQL Server database (ADR-0003), so a
/// scheduled job survives an application restart. The recurring sweep of §14
/// exists for the case it does not — a job lost to a deploy — and is never
/// the primary path.
/// </para>
///
/// <para>
/// A deadline already in the past is enqueued for immediate execution rather
/// than dropped: Hangfire treats a non-positive delay as "run now", which is
/// the correct behaviour when a clock starts on an already-elapsed target
/// (a provisional ticket reconciled long after its outage, for instance).
/// </para>
/// </summary>
public sealed class HangfireSlaDeadlineScheduler(IBackgroundJobClient backgroundJobClient) : ISlaDeadlineScheduler
{
    public void ScheduleDeadlineCheck(long ticketId, SlaDeadlineType deadlineType, DateTime dueAtUtc)
    {
        var delay = dueAtUtc - DateTime.UtcNow;

        backgroundJobClient.Schedule<SlaDeadlineCheckJob>(
            job => job.RunAsync(ticketId, deadlineType, CancellationToken.None),
            delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
    }
}

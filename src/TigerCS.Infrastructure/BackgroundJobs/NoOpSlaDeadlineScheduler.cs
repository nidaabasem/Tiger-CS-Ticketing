using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;
using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// The scheduler used when Hangfire is switched off
/// (<see cref="BackgroundJobOptions.Enabled"/>).
///
/// <para>
/// Enqueuing nothing is safe by design rather than by luck: scheduling is an
/// optimisation over SLA-Architecture.md §14's sweep, not the guarantee, and
/// both paths converge on the same idempotency key (§15). A deployment with
/// jobs disabled simply detects a breach on the next sweep tick instead of at
/// the exact due moment — and detects it exactly once either way.
/// </para>
/// </summary>
public sealed class NoOpSlaDeadlineScheduler : ISlaDeadlineScheduler
{
    public void ScheduleDeadlineCheck(long ticketId, SlaDeadlineType deadlineType, DateTime dueAtUtc)
    {
        // Intentionally empty — see this type's remarks.
    }
}

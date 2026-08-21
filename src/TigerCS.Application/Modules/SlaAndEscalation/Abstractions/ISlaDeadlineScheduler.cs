using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Application.Modules.SlaAndEscalation.Abstractions;

/// <summary>
/// ADR-0015 / SLA-Architecture.md §13 — the primary breach-detection
/// mechanism: a scheduled (delayed) background job enqueued per due timestamp
/// at the moment it is computed, so the check fires at the exact due moment
/// rather than waiting for a poll.
///
/// <para>
/// Abstracted here so the application layer never references Hangfire
/// directly (Module-Design.md's dependency rules) and so the correctness of
/// breach detection can be tested without a scheduler at all — the work a
/// fired job performs lives in <c>SlaBreachDetectionAppService</c>, which is
/// a plain service with no scheduling concern of its own.
/// </para>
///
/// <para>
/// <b>Scheduling is an optimisation, never the guarantee.</b>
/// SLA-Architecture.md §14's recurring sweep independently re-scans due
/// deadlines, so a job lost to a deploy or restart — or never enqueued at all,
/// which is what the no-op implementation does — still results in the breach
/// being detected. Both paths converge on the same idempotency key
/// (§15), so whichever runs second changes nothing.
/// </para>
/// </summary>
public interface ISlaDeadlineScheduler
{
    /// <summary>Enqueues a breach check for one ticket deadline, to run at <paramref name="dueAtUtc"/>.</summary>
    void ScheduleDeadlineCheck(long ticketId, SlaDeadlineType deadlineType, DateTime dueAtUtc);
}

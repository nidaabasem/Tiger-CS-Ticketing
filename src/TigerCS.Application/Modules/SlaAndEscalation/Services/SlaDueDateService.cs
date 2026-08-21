using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.SlaAndEscalation.Services;

/// <summary>
/// Opens a ticket's initial SLA period: computes both due timestamps from the
/// seeded policy and calendar, persists them as the ticket's current
/// <see cref="TicketSlaInstance"/>, audits the computation, and schedules the
/// two deadline checks (backlog S-08/S-09, FR-SLA-01–04).
///
/// <para>
/// <b>Participates in the caller's transaction.</b> Like
/// <c>IAuditEntryWriter</c>, nothing here calls SaveChanges — ticket creation
/// and CRM reconciliation each already run inside one transaction, and the
/// SLA period must commit or roll back with the ticket it belongs to.
/// </para>
///
/// <para>
/// <b>The clock-start event is ticket creation</b> (ISSUE-001 Option C,
/// SLA-Architecture.md §1/§2: the clock starts at creation, with
/// time-to-assignment tracked separately as a non-blocking metric). For a
/// provisional ticket created during a CRM outage the clock instead starts at
/// reconciliation, because FR-TKT-09 states an unverified ticket "does not
/// start its SLA clock" — the caller passes whichever moment applies.
/// </para>
/// </summary>
public sealed class SlaDueDateService(
    ISlaPolicyRepository slaPolicyRepository,
    IBusinessCalendarRepository businessCalendarRepository,
    ITicketSlaInstanceRepository slaInstanceRepository,
    ISlaDeadlineScheduler deadlineScheduler,
    IAuditEntryWriter auditWriter)
{
    /// <summary>
    /// Computes and stores the ticket's first SLA period.
    /// </summary>
    /// <param name="ticket">The ticket whose clock is starting. Its <c>PriorityId</c> selects the policy.</param>
    /// <param name="clockStartAtUtc">The approved clock-start moment — see this type's remarks.</param>
    /// <param name="actorEmployeeId">The employee whose action started the clock, for the audit entry. Null for a system action.</param>
    /// <param name="correlationId">Shared with the caller's other audit rows so one action reads as one event (NFR-REL-03).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<TicketSlaInstance> OpenInitialPeriodAsync(
        Ticket ticket,
        DateTime clockStartAtUtc,
        Guid? actorEmployeeId,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        var (firstResponseDueAtUtc, resolutionDueAtUtc) =
            await ComputeDueDatesAsync(ticket.PriorityId, clockStartAtUtc, cancellationToken);

        var instance = TicketSlaInstance.OpenInitialPeriod(
            ticket.TicketId, ticket.PriorityId, clockStartAtUtc, firstResponseDueAtUtc, resolutionDueAtUtc);

        await slaInstanceRepository.AddAsync(instance, cancellationToken);

        // FR-SLA-04 / NFR-LOG-01: the computation itself is auditable, not
        // only its result — "structured logging sufficient to reconstruct any
        // SLA/escalation calculation for audit or dispute".
        await auditWriter.WriteAsync(
            actorEmployeeId,
            "ComputeSlaDueDates",
            nameof(TicketSlaInstance),
            ticket.TicketId.ToString(),
            beforeValue: null,
            afterValue:
                $"{{\"priorityId\":{ticket.PriorityId},\"clockStartAtUtc\":\"{clockStartAtUtc:O}\","
                + $"\"firstResponseDueAtUtc\":\"{firstResponseDueAtUtc:O}\",\"resolutionDueAtUtc\":\"{resolutionDueAtUtc:O}\"}}",
            correlationId,
            cancellationToken);

        // SLA-Architecture.md §13 — the primary detection mechanism, enqueued
        // at the moment the due timestamp is computed. §14's recurring sweep
        // independently covers a job lost to a deploy or restart, so this is
        // an optimisation and never the guarantee.
        deadlineScheduler.ScheduleDeadlineCheck(ticket.TicketId, SlaDeadlineType.FirstResponse, firstResponseDueAtUtc);
        deadlineScheduler.ScheduleDeadlineCheck(ticket.TicketId, SlaDeadlineType.Resolution, resolutionDueAtUtc);

        return instance;
    }

    /// <summary>
    /// The pair of due timestamps for one priority and clock-start moment.
    /// Exposed separately from <see cref="OpenInitialPeriodAsync"/> so the
    /// computation can be exercised — and reused by backlog S-14's priority
    /// upgrade — without writing anything.
    /// </summary>
    public async Task<(DateTime FirstResponseDueAtUtc, DateTime ResolutionDueAtUtc)> ComputeDueDatesAsync(
        byte priorityId, DateTime clockStartAtUtc, CancellationToken cancellationToken = default)
    {
        var policy = await slaPolicyRepository.GetByPriorityIdAsync(priorityId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"No SlaPolicy is seeded for PriorityId {priorityId}. MVP-ERD.md §2.6 requires exactly one policy row per priority.");

        // Loaded only when a tier actually needs it: SLA-Architecture.md §3
        // has Critical bypass the calendar entirely, and a Critical ticket
        // must keep working even if calendar reference data is missing.
        var calendar = policy.ClockBasis == SlaClockBasis.BusinessHours
            ? await businessCalendarRepository.GetActiveSnapshotAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    "No active BusinessCalendar is seeded. A business-hours SLA tier cannot be computed without one (ADR-0010).")
            : null;

        var firstResponseDueAtUtc = SlaDueDateCalculator.ComputeDueAtUtc(
            clockStartAtUtc, policy.TargetMinutesFor(SlaDeadlineType.FirstResponse), policy.ClockBasis, calendar);

        var resolutionDueAtUtc = SlaDueDateCalculator.ComputeDueAtUtc(
            clockStartAtUtc, policy.TargetMinutesFor(SlaDeadlineType.Resolution), policy.ClockBasis, calendar);

        return (firstResponseDueAtUtc, resolutionDueAtUtc);
    }
}

using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

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
    IAuditEntryWriter auditWriter,
    // Always supplied by DI. Null only in unit fixtures that predate
    // configuration enforcement: every due date then comes from the
    // per-priority policy, exactly as before.
    IRequestTypeRepository? requestTypeRepository = null,
    IRequestTypeSlaPolicyRepository? requestTypeSlaPolicyRepository = null)
{
    /// <summary>Both due timestamps, and which configuration each came from (for the audit trail).</summary>
    public sealed record SlaDueDates(
        DateTime FirstResponseDueAtUtc, DateTime ResolutionDueAtUtc, string FirstResponseSource, string ResolutionSource);

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

        // The invariant that makes TicketSlaInstance.PriorityId non-nullable:
        // an SLA period only ever exists for a classified ticket, because the
        // policy is selected by priority and an unclassified ticket has none.
        // Callers open the period at classification, never before — this is
        // the assertion, not a fallback.
        var priorityId = ticket.PriorityId
            ?? throw new InvalidOperationException(
                $"Ticket {ticket.TicketId} has no priority — an SLA period may only be opened for a classified ticket.");

        var dueDates = await ComputeAsync(priorityId, ticket.RequestTypeId, clockStartAtUtc, cancellationToken);
        var (firstResponseDueAtUtc, resolutionDueAtUtc) = (dueDates.FirstResponseDueAtUtc, dueDates.ResolutionDueAtUtc);

        var instance = TicketSlaInstance.OpenInitialPeriod(
            ticket.TicketId, priorityId, clockStartAtUtc, firstResponseDueAtUtc, resolutionDueAtUtc);

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
                $"{{\"priorityId\":{priorityId},\"clockStartAtUtc\":\"{clockStartAtUtc:O}\","
                + $"\"firstResponseDueAtUtc\":\"{firstResponseDueAtUtc:O}\",\"resolutionDueAtUtc\":\"{resolutionDueAtUtc:O}\","
                + $"\"firstResponseSource\":\"{dueDates.FirstResponseSource}\",\"resolutionSource\":\"{dueDates.ResolutionSource}\"}}",
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
    /// The approved Reopen rule's SLA behaviour: end the ticket's current SLA
    /// period at the reopen moment and open a successor whose
    /// <b>Resolution</b> clock starts there, computed from the same policy and
    /// calendar as any other period — never a hard-coded duration.
    ///
    /// <para>
    /// <b>First Response is never restarted.</b> Its due timestamp and breach
    /// flag are carried across verbatim (see
    /// <see cref="TicketSlaInstance.OpenReopenCycle"/>), and no First Response
    /// deadline check is scheduled: that clock's result was settled on the
    /// original cycle and reopening must not re-open, re-arm or re-flag it.
    /// </para>
    ///
    /// <para>
    /// <b>This is what stops a reopened ticket breaching instantly.</b> The
    /// old period's long-past ResolutionDueAtUtc leaves the sweep's candidate
    /// set the moment that period is ended (the sweep reads current periods
    /// only), and the successor carries a deadline in the future — so the
    /// reopened ticket is neither auto-breached nor auto-escalated on the
    /// strength of a deadline that belonged to work already delivered. The old
    /// row keeps its own flags untouched, so the original cycle stays
    /// honestly reportable (ISSUE-023).
    /// </para>
    ///
    /// <para>
    /// Participates in the caller's transaction, like
    /// <see cref="OpenInitialPeriodAsync"/>. Returns null — writing nothing —
    /// when the ticket has no current period at all (a provisional ticket
    /// still awaiting reconciliation, or an unclassified one): reopening such
    /// a ticket must not invent an SLA clock it never had.
    /// </para>
    /// </summary>
    /// <param name="ticket">The ticket being reopened. Its <c>PriorityId</c> selects the policy.</param>
    /// <param name="reopenedAtUtc">The reopen moment — where the old period ends and the new Resolution clock starts.</param>
    /// <param name="actorEmployeeId">The reopening agent, for the audit entry.</param>
    /// <param name="correlationId">Shared with the reopen's other rows so one action reads as one event.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The newly opened cycle, or null when the ticket has no SLA period to succeed.</returns>
    public async Task<TicketSlaInstance?> StartReopenResolutionCycleAsync(
        Ticket ticket,
        DateTime reopenedAtUtc,
        Guid? actorEmployeeId,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        var current = await slaInstanceRepository.GetCurrentAsync(ticket.TicketId, cancellationToken);
        if (current is null || ticket.PriorityId is not { } priorityId)
        {
            return null;
        }

        // The same precedence as the initial period; only the Resolution
        // due date is used — First Response is carried, never recomputed.
        var reopenDueDates = await ComputeAsync(priorityId, ticket.RequestTypeId, reopenedAtUtc, cancellationToken);
        var resolutionDueAtUtc = reopenDueDates.ResolutionDueAtUtc;

        // Order matters: the one-current-period-per-ticket filtered unique
        // index would reject the successor otherwise.
        current.EndPeriod(reopenedAtUtc);

        var cycle = TicketSlaInstance.OpenReopenCycle(
            ticket.TicketId,
            priorityId,
            reopenedAtUtc,
            carriedFirstResponseDueAtUtc: current.FirstResponseDueAtUtc,
            carriedFirstResponseBreached: current.FirstResponseBreached,
            resolutionDueAtUtc);

        await slaInstanceRepository.AddAsync(cycle, cancellationToken);

        // FR-SLA-04 / NFR-LOG-01, same as the initial period: the computation
        // is auditable, not only its result. The carried first-response
        // values are recorded explicitly so a dispute can see that the
        // original clock was preserved rather than recomputed.
        await auditWriter.WriteAsync(
            actorEmployeeId,
            "ComputeSlaDueDates",
            nameof(TicketSlaInstance),
            ticket.TicketId.ToString(),
            beforeValue:
                $"{{\"changeReason\":\"{SlaChangeReason.Reopen}\",\"endedPeriodStartAtUtc\":\"{current.PeriodStartAtUtc:O}\","
                + $"\"endedResolutionDueAtUtc\":\"{current.ResolutionDueAtUtc:O}\",\"endedResolutionBreached\":{(current.ResolutionBreached ? "true" : "false")}}}",
            afterValue:
                $"{{\"priorityId\":{priorityId},\"clockStartAtUtc\":\"{reopenedAtUtc:O}\","
                + $"\"resolutionDueAtUtc\":\"{resolutionDueAtUtc:O}\",\"resolutionSource\":\"{reopenDueDates.ResolutionSource}\","
                + $"\"firstResponseDueAtUtc\":\"{cycle.FirstResponseDueAtUtc:O}\",\"firstResponseCarried\":true,"
                + $"\"firstResponseBreached\":{(cycle.FirstResponseBreached ? "true" : "false")}}}",
            correlationId,
            cancellationToken);

        // Only the Resolution check is scheduled. Scheduling a First Response
        // check here would be the re-arming this method exists to prevent.
        deadlineScheduler.ScheduleDeadlineCheck(ticket.TicketId, SlaDeadlineType.Resolution, resolutionDueAtUtc);

        return cycle;
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
        var dueDates = await ComputeAsync(priorityId, requestTypeId: null, clockStartAtUtc, cancellationToken);
        return (dueDates.FirstResponseDueAtUtc, dueDates.ResolutionDueAtUtc);
    }

    /// <summary>
    /// The due dates for one priority, request type and clock-start moment.
    ///
    /// <para>
    /// <b>Precedence.</b> When the ticket's request type has
    /// <see cref="RequestType.ConfigurationEnforced"/> on and an active
    /// <see cref="RequestTypeSlaPolicy"/> row exists for the ticket's
    /// priority, each deadline that row defines is computed from it — its own
    /// value, unit (First Response may have its own unit) and clock basis.
    /// A deadline the row leaves empty, a priority without a row, a request
    /// type without enforcement, and a row that cannot be applied at runtime
    /// (<see cref="ConfiguredRuntimeReadiness.SlaRowIssues"/>) all use the
    /// per-priority <see cref="SlaPolicy"/>, exactly as before. The clock
    /// start, the reopen restart and the carried First Response are the
    /// callers' established rules and do not change with the source.
    /// </para>
    ///
    /// <para>
    /// <b>Units on the business calendar.</b> With the BusinessHours basis
    /// a configured day is one working-day window of the active business
    /// calendar and an hour is 60 business minutes, walked by the same
    /// <see cref="SlaDueDateCalculator"/> as every other SLA (working days,
    /// holidays and the window all apply); with 24/7 a day is 24 hours.
    /// </para>
    /// </summary>
    public async Task<SlaDueDates> ComputeAsync(
        byte priorityId, int? requestTypeId, DateTime clockStartAtUtc, CancellationToken cancellationToken = default)
    {
        var policy = await slaPolicyRepository.GetByPriorityIdAsync(priorityId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"No SlaPolicy is seeded for PriorityId {priorityId}. MVP-ERD.md §2.6 requires exactly one policy row per priority.");

        var configured = await ConfiguredRowAsync(requestTypeId, priorityId, cancellationToken);
        var configuredBasis = configured?.ClockBasis;

        // Loaded only when a basis actually needs it: SLA-Architecture.md §3
        // has Critical bypass the calendar entirely, and a Critical ticket
        // must keep working even if calendar reference data is missing.
        var needsCalendar = policy.ClockBasis == SlaClockBasis.BusinessHours || configuredBasis == SlaClockBasis.BusinessHours;
        var calendar = needsCalendar
            ? await businessCalendarRepository.GetActiveSnapshotAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    "No active BusinessCalendar is seeded. A business-hours SLA tier cannot be computed without one (ADR-0010).")
            : null;

        const string policySource = "PriorityPolicy";
        var configuredSource = configured is null ? policySource : $"RequestTypeSla:{configured.RequestTypeSlaPolicyId}";

        (DateTime DueAtUtc, string Source) Deadline(SlaDeadlineType deadlineType, int? configuredValue, SlaDurationUnit configuredUnit)
        {
            if (configured is not null && configuredValue is { } value && configuredBasis is { } basis)
            {
                var minutes = ConfiguredRuntimeReadiness.ToMinutes(value, configuredUnit, basis, calendar?.BusinessDayLength ?? TimeSpan.Zero);
                return (SlaDueDateCalculator.ComputeDueAtUtc(clockStartAtUtc, minutes, basis, calendar), configuredSource);
            }

            return (SlaDueDateCalculator.ComputeDueAtUtc(clockStartAtUtc, policy.TargetMinutesFor(deadlineType), policy.ClockBasis, calendar),
                policySource);
        }

        var firstResponse = Deadline(SlaDeadlineType.FirstResponse, configured?.FirstResponseTargetValue,
            configured?.EffectiveFirstResponseUnit ?? SlaDurationUnit.Minutes);
        var resolution = Deadline(SlaDeadlineType.Resolution, configured?.ResolutionTargetValue,
            configured?.Unit ?? SlaDurationUnit.Minutes);

        return new SlaDueDates(firstResponse.DueAtUtc, resolution.DueAtUtc, firstResponse.Source, resolution.Source);
    }

    /// <summary>The enforced request type's applicable SLA row for this priority, or null when the per-priority policy governs.</summary>
    private async Task<RequestTypeSlaPolicy?> ConfiguredRowAsync(int? requestTypeId, byte priorityId, CancellationToken cancellationToken)
    {
        if (requestTypeId is not { } id || requestTypeRepository is null || requestTypeSlaPolicyRepository is null)
        {
            return null;
        }

        var requestType = await requestTypeRepository.GetByIdAsync(id, cancellationToken);
        if (requestType is not { ConfigurationEnforced: true })
        {
            return null;
        }

        var row = await requestTypeSlaPolicyRepository.GetActiveAsync(id, priorityId, cancellationToken);
        return row is not null && ConfiguredRuntimeReadiness.SlaRowIssues(row).Count == 0 ? row : null;
    }
}

using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.SlaAndEscalation.Services;

/// <summary>What <see cref="SlaPauseService.SyncAsync"/> did.</summary>
public enum SlaPauseOutcome
{
    /// <summary>The ticket has no current SLA period (provisional or unclassified) — nothing to pause or resume.</summary>
    NoSlaPeriod,

    /// <summary>The status neither pauses nor needs a resume: nothing changed.</summary>
    NoChange,

    /// <summary>A Resolution pause was opened.</summary>
    Paused,

    /// <summary>The ticket is already paused — an idempotent repeat that wrote nothing.</summary>
    AlreadyPaused,

    /// <summary>The ticket is in a Pending status but its Resolution clock may not pause; <see cref="SlaPauseResult.Detail"/> says why.</summary>
    NotPausable,

    /// <summary>An open pause was closed and the Resolution deadline extended by the paused time (returned to work).</summary>
    Resumed,

    /// <summary>An open pause was closed at the Resolved/Closed instant (deadline extended first, so the achievement is judged against the true deadline).</summary>
    ClosedOutByResolution
}

/// <param name="Outcome">What happened.</param>
/// <param name="Detail">A short human-readable reason (why a pause was refused, or the new deadline).</param>
/// <param name="ResolutionDueAtUtc">The Resolution deadline after the call (the period's current one).</param>
public sealed record SlaPauseResult(SlaPauseOutcome Outcome, string? Detail = null, DateTime? ResolutionDueAtUtc = null);

/// <summary>
/// SLA pause/resume (ISSUE-018, SLA-Architecture.md §6/§8) — the one writer of
/// <see cref="TicketSlaPausePeriod"/>.
///
/// <para>
/// <b>Driven by the ticket's status, never by a separate command.</b> A
/// lifecycle service mutates <see cref="Ticket.TicketStatus"/> and then calls
/// <see cref="SyncAsync"/>; this service compares the status with the
/// ticket's open pause and does whatever is needed: open one, close one, or
/// nothing. That makes every entry point idempotent by construction — a
/// repeated Pending Customer entry finds the open pause and writes nothing, a
/// duplicate resume finds none and writes nothing — and means the same code
/// handles the ordinary status change, Resolve, Close and the system
/// inactivity closure.
/// </para>
///
/// <para>
/// <b>What pauses.</b> Only the <b>Resolution</b> clock, only while the ticket
/// is Pending Customer (or legacy Pending Third Party), never a Critical
/// period, and not when the request type's explicit
/// <c>PausesOnPendingCustomer</c> is <c>false</c>
/// (<see cref="SlaPauseRules"/>). First Response never pauses. A deadline
/// already missed is recorded as a breach <i>before</i> any pause opens, and a
/// period that has already breached Resolution does not pause (the breach is
/// permanent; extending its deadline would rewrite history).
/// </para>
///
/// <para>
/// <b>On resume the deadline moves, nothing else does.</b> See
/// <see cref="SlaPauseCalculator"/>. Elapsed time, breach flags and period
/// history are untouched; the pause row keeps the deadline before and after.
/// The new deadline is handed to <see cref="ISlaDeadlineScheduler"/>; the job
/// scheduled for the old one is harmless — it fires, finds the extended
/// deadline in the future, and records nothing.
/// </para>
///
/// <para>
/// Participates in the caller's transaction — nothing here calls SaveChanges.
/// </para>
/// </summary>
public sealed class SlaPauseService(
    ITicketSlaInstanceRepository slaInstanceRepository,
    ITicketSlaPausePeriodRepository pauseRepository,
    ISlaPolicyRepository slaPolicyRepository,
    IBusinessCalendarRepository businessCalendarRepository,
    ISlaDeadlineScheduler deadlineScheduler,
    SlaBreachProcessor breachProcessor,
    ITicketStatusHistoryRepository statusHistoryRepository,
    IAuditEntryWriter auditWriter)
{
    /// <summary>
    /// Brings the ticket's pause state in line with its (already updated)
    /// <see cref="Ticket.TicketStatus"/>.
    /// </summary>
    public async Task<SlaPauseResult> SyncAsync(
        Ticket ticket,
        DateTime nowUtc,
        Guid? actorEmployeeId,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        var instance = await slaInstanceRepository.GetCurrentAsync(ticket.TicketId, cancellationToken);
        var open = await pauseRepository.GetOpenAsync(ticket.TicketId, cancellationToken);

        if (SlaPauseRules.IsPausingStatus(ticket.TicketStatus))
        {
            if (instance is null)
            {
                return new SlaPauseResult(SlaPauseOutcome.NoSlaPeriod);
            }

            return open is not null
                ? new SlaPauseResult(SlaPauseOutcome.AlreadyPaused, ResolutionDueAtUtc: instance.ResolutionDueAtUtc)
                : await PauseAsync(ticket, instance, nowUtc, actorEmployeeId, correlationId, cancellationToken);
        }

        if (open is null)
        {
            return new SlaPauseResult(instance is null ? SlaPauseOutcome.NoSlaPeriod : SlaPauseOutcome.NoChange);
        }

        var endedByResolution = ticket.TicketStatus is TicketStatus.Resolved or TicketStatus.Closed;
        return await CloseOpenPauseAsync(ticket, instance, open, nowUtc, actorEmployeeId, correlationId, endedByResolution, cancellationToken);
    }

    /// <summary>
    /// Closes whatever pause is open on the ticket without regard to its
    /// status — the safety valve for flows that end or replace the SLA
    /// period (reopen, priority change): no pause may be left dangling on a
    /// period that is going away. Idempotent.
    /// </summary>
    public async Task<SlaPauseResult> CloseOpenPauseAsync(
        Ticket ticket,
        DateTime nowUtc,
        Guid? actorEmployeeId,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        var open = await pauseRepository.GetOpenAsync(ticket.TicketId, cancellationToken);
        if (open is null)
        {
            return new SlaPauseResult(SlaPauseOutcome.NoChange);
        }

        // Safety valve for damaged/leftover data: the pause is closed so none
        // dangles, but the deadline is NOT extended — the real pause ended
        // earlier (at resolve/close) and its extension, if any, was applied
        // then; guessing a length now would rewrite a closed period.
        var instance = await slaInstanceRepository.GetCurrentAsync(ticket.TicketId, cancellationToken);
        return await CloseOpenPauseAsync(
            ticket, instance, open, nowUtc, actorEmployeeId, correlationId, endedByResolution: true, cancellationToken,
            extendDeadline: false);
    }

    private async Task<SlaPauseResult> PauseAsync(
        Ticket ticket,
        TicketSlaInstance instance,
        DateTime nowUtc,
        Guid? actorEmployeeId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (!SlaPauseRules.ResolutionPauses(instance.PriorityId, ticket.TicketStatus, instance.PausesOnPendingCustomerOverride))
        {
            var why = instance.PriorityId == (byte)PriorityLevel.Critical
                ? "the Critical SLA never pauses"
                : "the request type's SLA is configured not to pause on Pending Customer";
            return new SlaPauseResult(SlaPauseOutcome.NotPausable, why, instance.ResolutionDueAtUtc);
        }

        if (instance.ResolutionBreached)
        {
            return new SlaPauseResult(
                SlaPauseOutcome.NotPausable, "the Resolution deadline has already been breached", instance.ResolutionDueAtUtc);
        }

        // A deadline that was missed before this moment must be recorded as
        // missed — pausing now would otherwise let a late job-less ticket
        // slip past its breach by going Pending just after the due time.
        if (nowUtc >= instance.ResolutionDueAtUtc)
        {
            var outcome = await breachProcessor.ProcessDeadlineAsync(
                ticket, SlaDeadlineType.Resolution, nowUtc, correlationId, cancellationToken);
            if (outcome == SlaBreachProcessingOutcome.BreachRecorded || instance.ResolutionBreached)
            {
                return new SlaPauseResult(
                    SlaPauseOutcome.NotPausable, "the Resolution deadline had already passed and was recorded as breached",
                    instance.ResolutionDueAtUtc);
            }
        }

        var reason = SlaPauseRules.ReasonFor(ticket.TicketStatus);
        await pauseRepository.AddAsync(
            new TicketSlaPausePeriod(ticket.TicketId, instance.TicketSlaInstanceId, reason, nowUtc, instance.ResolutionDueAtUtc),
            cancellationToken);

        var previousState = ticket.SlaState;
        if (ticket.PauseSla())
        {
            await AddSlaStateHistoryAsync(
                ticket, previousState, actorEmployeeId, correlationId, nowUtc,
                $"Resolution SLA paused ({ReasonText(reason)}); deadline held at {instance.ResolutionDueAtUtc:O}.", cancellationToken);
        }

        await auditWriter.WriteAsync(
            actorEmployeeId, "PauseSla", nameof(TicketSlaPausePeriod), ticket.TicketId.ToString(),
            beforeValue: $"{{\"slaState\":\"{previousState}\"}}",
            afterValue:
                $"{{\"reason\":\"{reason}\",\"startedAtUtc\":\"{nowUtc:O}\",\"resolutionDueAtUtc\":\"{instance.ResolutionDueAtUtc:O}\","
                + $"\"ticketSlaInstanceId\":{instance.TicketSlaInstanceId}}}",
            correlationId, cancellationToken);

        return new SlaPauseResult(SlaPauseOutcome.Paused, ReasonText(reason), instance.ResolutionDueAtUtc);
    }

    private async Task<SlaPauseResult> CloseOpenPauseAsync(
        Ticket ticket,
        TicketSlaInstance? currentInstance,
        TicketSlaPausePeriod open,
        DateTime nowUtc,
        Guid? actorEmployeeId,
        Guid correlationId,
        bool endedByResolution,
        CancellationToken cancellationToken,
        bool extendDeadline = true)
    {
        // The pause belongs to the current period unless a flow replaced the
        // period without closing it — then the new period never ran this
        // pause, and only the pause row itself is closed.
        var belongsToCurrent = currentInstance is not null && open.TicketSlaInstanceId == currentInstance.TicketSlaInstanceId;
        var dueBefore = belongsToCurrent ? currentInstance!.ResolutionDueAtUtc : open.ResolutionDueBeforeAtUtc;
        var dueAfter = dueBefore;

        if (extendDeadline && belongsToCurrent && !currentInstance!.ResolutionBreached)
        {
            var basis = await ResolveClockBasisAsync(currentInstance, cancellationToken);
            var calendar = basis == SlaClockBasis.BusinessHours
                ? await businessCalendarRepository.GetActiveSnapshotAsync(cancellationToken)
                : null;

            // A business-hours deadline cannot be re-walked without its
            // calendar. Missing reference data must not strand the pause open
            // (or the ticket in an unresolvable state), so fall back to adding
            // the wall-clock paused time — the approved wording of §8 — rather
            // than throw.
            dueAfter = basis == SlaClockBasis.BusinessHours && calendar is null
                ? SlaPauseCalculator.ExtendedDueAtUtc(dueBefore, open.StartedAtUtc, nowUtc, SlaClockBasis.TwentyFourSeven, null)
                : SlaPauseCalculator.ExtendedDueAtUtc(dueBefore, open.StartedAtUtc, nowUtc, basis, calendar);

            currentInstance.ExtendResolutionDue(dueAfter);
        }

        open.Close(nowUtc, dueAfter, endedByResolution);

        var previousState = ticket.SlaState;
        if (ticket.ResumeSla())
        {
            await AddSlaStateHistoryAsync(
                ticket, previousState, actorEmployeeId, correlationId, nowUtc,
                endedByResolution
                    ? $"Resolution SLA pause closed at {ticket.TicketStatus}; deadline moved from {dueBefore:O} to {dueAfter:O}."
                    : $"Resolution SLA resumed; deadline moved from {dueBefore:O} to {dueAfter:O}.",
                cancellationToken);
        }

        await auditWriter.WriteAsync(
            actorEmployeeId, "ResumeSla", nameof(TicketSlaPausePeriod), ticket.TicketId.ToString(),
            beforeValue: $"{{\"startedAtUtc\":\"{open.StartedAtUtc:O}\",\"resolutionDueAtUtc\":\"{dueBefore:O}\"}}",
            afterValue:
                $"{{\"resumedAtUtc\":\"{nowUtc:O}\",\"pausedMinutes\":{open.DurationAsOf(nowUtc).TotalMinutes:F2},"
                + $"\"resolutionDueAtUtc\":\"{dueAfter:O}\",\"endedByResolution\":{(endedByResolution ? "true" : "false")}}}",
            correlationId, cancellationToken);

        // The deadline job scheduled for the old due time cannot be cancelled
        // through the abstraction; it is harmless (it re-reads the extended
        // deadline). A job for the new one is what makes detection exact.
        if (!endedByResolution && belongsToCurrent && dueAfter != dueBefore)
        {
            deadlineScheduler.ScheduleDeadlineCheck(ticket.TicketId, SlaDeadlineType.Resolution, dueAfter);
        }

        return new SlaPauseResult(
            endedByResolution ? SlaPauseOutcome.ClosedOutByResolution : SlaPauseOutcome.Resumed,
            $"deadline {dueBefore:O} -> {dueAfter:O}",
            belongsToCurrent ? dueAfter : null);
    }

    private async Task<SlaClockBasis> ResolveClockBasisAsync(TicketSlaInstance instance, CancellationToken cancellationToken)
    {
        if (instance.ResolutionClockBasis is { } stored)
        {
            return stored;
        }

        // A period opened before the basis was recorded: it was computed from
        // the per-priority policy.
        var policy = await slaPolicyRepository.GetByPriorityIdAsync(instance.PriorityId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"No SlaPolicy is seeded for PriorityId {instance.PriorityId}; the clock basis of SLA period {instance.TicketSlaInstanceId} cannot be determined.");
        return policy.ClockBasis;
    }

    private async Task AddSlaStateHistoryAsync(
        Ticket ticket, SlaState previous, Guid? actorEmployeeId, Guid correlationId, DateTime nowUtc, string note,
        CancellationToken cancellationToken) =>
        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticket.TicketId, TicketStatusDimension.SlaState, (byte)previous, (byte)ticket.SlaState,
                actorEmployeeId, actorIsSystem: actorEmployeeId is null, note, correlationId, nowUtc),
            cancellationToken);

    internal static string ReasonText(SlaPauseReason reason) => reason switch
    {
        SlaPauseReason.PendingCustomer => "Pending Customer",
        SlaPauseReason.PendingThirdPartyLegacy => "Pending Third Party (legacy)",
        _ => reason.ToString()
    };
}

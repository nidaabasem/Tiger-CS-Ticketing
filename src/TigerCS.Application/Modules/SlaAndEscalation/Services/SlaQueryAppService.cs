using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.SlaAndEscalation.Services;

/// <summary>
/// Read-side of the SLA and escalation module (MVP-API-Contracts.md
/// §5.1/§5.9).
///
/// <para>
/// Department visibility is the same rule the ticket queue and detail
/// endpoints use, reached through <c>TicketQueryAppService</c> rather than
/// re-implemented: an SLA panel is ticket data, so a caller who cannot see
/// the ticket must not learn its SLA position or its escalation history
/// either (Security-Architecture.md §3).
/// </para>
/// </summary>
public sealed class SlaQueryAppService(
    ITicketRepository ticketRepository,
    ITicketSlaInstanceRepository slaInstanceRepository,
    ITicketEscalationRepository escalationRepository,
    TicketQueryAppService ticketQueryAppService,
    ISlaPolicyRepository slaPolicyRepository,
    IBusinessCalendarRepository businessCalendarRepository,
    ITicketSlaPausePeriodRepository? pauseRepository = null,
    IRequestTypeSlaPolicyRepository? requestTypeSlaPolicyRepository = null,
    TimeProvider? timeProvider = null)
{
    // ---- The implementation facts the explanation states. One place, so the
    // ticket panel and the administration view never disagree. Each sentence
    // describes what the code does TODAY (SlaDueDateService,
    // TicketClassificationAppService, FirstHumanResponseRecorder,
    // AgentHandoffAppService); change the code, change the sentence.

    public const string PolicySourcePriority = "Per-priority SLA policy";

    public const string ClockStartRule =
        "The SLA clock starts when the ticket has a category and a priority: at creation for a ticket created classified, "
        + "or at the moment of classification for a ticket created unclassified (every Genesys call, chat or message starts unclassified). "
        + "The clock is never backdated to the interaction or creation time. A provisional ticket awaiting CRM reconciliation starts when it is reconciled.";

    public const string PolicySourceRequestType = "Request-type SLA policy (per-priority policy for anything it does not set)";

    public const string PolicyPrecedence =
        "Targets come from the per-priority SLA policy (Critical, High, Medium, Low). "
        + "A request type's own SLA values override them only where they are unambiguous: the row is active, starts at ticket creation, has an explicit clock basis, "
        + "and each duration is a single value (not a range). Anything else is not applied: the per-priority policy is used and the ticket's SLA panel states the exact reason "
        + "(range interpretation, calendar-vs-business days, or a non-creation trigger is still undecided). "
        + "First response always comes from the per-priority policy unless the request type sets a single value. No department-level SLA exists.";

    public const string FirstResponseRule =
        "First response is satisfied only by a recorded human response: a human agent's message in the Genesys conversation transcript, or an explicit 'record first response' call. "
        + "Accepting a pending interaction (Accept & Start / Start Handling), assigning the ticket, changing its status or the automated acknowledgement do NOT count.";

    public const string PauseRule =
        "The Resolution SLA pauses while a non-Critical ticket is Pending Customer (and, for legacy tickets, Pending Third Party) and resumes when it returns to In Progress; "
        + "the due time is extended by the time paused, and elapsed time, breaches and history are never erased. "
        + "The Critical SLA never pauses, and the First Response SLA never pauses (it cannot once a first human response is recorded). "
        + "A request type can explicitly set 'pauses on Pending Customer' to Yes or No; when unset it follows this rule. "
        + "A pause still open when the ticket is resolved or closed ends at that moment. Pending Internal does not pause (undecided).";

    /// <summary>MVP-API-Contracts.md §5.1 — the ticket detail screen's SLA panel.</summary>
    public async Task<SlaOperationResult<TicketSlaSummaryResponseDto>> GetSummaryAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.NotFound);
        }

        if (!await ticketQueryAppService.CanViewDepartmentAsync(
                callerEmployeeId, callerRoles, ticket.CurrentDepartmentId, cancellationToken))
        {
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.Forbidden);
        }

        // Null for a provisional ticket still awaiting CRM reconciliation:
        // FR-TKT-09 keeps its clock unstarted, so there is no period and no
        // due timestamp to report. The response says so honestly rather than
        // fabricating a deadline.
        var instance = await slaInstanceRepository.GetCurrentAsync(ticketId, cancellationToken);

        var appliedPriority = instance?.PriorityId ?? ticket.PriorityId;
        var policy = appliedPriority is { } priorityId
            ? await slaPolicyRepository.GetByPriorityIdAsync(priorityId, cancellationToken)
            : null;
        var effectiveBasis = instance?.ResolutionClockBasis ?? policy?.ClockBasis;
        var calendar = effectiveBasis == SlaClockBasis.BusinessHours
            ? await businessCalendarRepository.GetActiveDescriptionAsync(cancellationToken)
            : null;

        // The request-type row is only evaluated live when no period exists
        // to carry the snapshot of what happened at open time (a period
        // records its own applied policy and not-applied reason).
        RequestTypeSlaEvaluation? liveRequestTypeSla = null;
        if (instance is null && ticket.RequestTypeId is { } requestTypeId && appliedPriority is { } liveP
            && requestTypeSlaPolicyRepository is not null)
        {
            var requestTypePolicy = await requestTypeSlaPolicyRepository.GetActiveAsync(requestTypeId, liveP, cancellationToken);
            var description = requestTypePolicy?.ClockBasis == SlaClockBasis.BusinessHours
                ? calendar ?? await businessCalendarRepository.GetActiveDescriptionAsync(cancellationToken)
                : null;
            liveRequestTypeSla = RequestTypeSlaEnforcement.Evaluate(requestTypePolicy, BusinessDayLengthOf(description));
        }

        var pause = await GetPauseSummaryAsync(instance, cancellationToken);

        return SlaOperationResult<TicketSlaSummaryResponseDto>.Success(
            ToSummaryDto(ticket, instance, pause)
            with { Explanation = BuildExplanation(ticket, instance, policy, calendar, liveRequestTypeSla, pause) });
    }

    /// <summary>The pause facts of the current period, for the summary fields and the explanation.</summary>
    public sealed record SlaPauseSummary(bool IsCurrentlyPaused, string? CurrentPauseReason, int TotalPausedMinutes);

    private async Task<SlaPauseSummary?> GetPauseSummaryAsync(TicketSlaInstance? instance, CancellationToken cancellationToken)
    {
        if (instance is null || pauseRepository is null)
        {
            return null;
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        return SummarizePauses(await pauseRepository.ListByInstanceIdAsync(instance.TicketSlaInstanceId, cancellationToken), now);
    }

    /// <summary>
    /// The §5.1 pause fields from one period's pause rows. An open pause is
    /// counted up to <paramref name="nowUtc"/>; minutes are floored, not
    /// rounded — a minute is only claimed once it has passed.
    /// </summary>
    public static SlaPauseSummary SummarizePauses(IReadOnlyCollection<TicketSlaPausePeriod> pauses, DateTime nowUtc)
    {
        var open = pauses.FirstOrDefault(p => p.IsOpen);
        var totalMinutes = (int)pauses.Sum(p => p.DurationAsOf(nowUtc).TotalMinutes);
        return new SlaPauseSummary(open is not null, open is null ? null : SlaPauseService.ReasonText(open.Reason), totalMinutes);
    }

    /// <summary>The §5.1 response shape from a ticket, its current period and that period's pause summary.</summary>
    public static TicketSlaSummaryResponseDto BuildSummary(Ticket ticket, TicketSlaInstance? instance, SlaPauseSummary? pause) =>
        ToSummaryDto(ticket, instance, pause);

    private static TimeSpan? BusinessDayLengthOf(SlaCalendarDto? calendar) =>
        calendar is not null
        && TimeOnly.TryParse(calendar.BusinessDayStartLocal, out var start)
        && TimeOnly.TryParse(calendar.BusinessDayEndLocal, out var end)
        && end > start
            ? end - start
            : null;

    /// <summary>
    /// The System Administrator's reference view of the SLA configuration the
    /// calculation applies, plus the implementation facts needed to read the
    /// request-type SLA values correctly. Read-only.
    /// </summary>
    public async Task<SlaConfigurationDto> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var policies = await slaPolicyRepository.ListAsync(cancellationToken);
        var calendar = await businessCalendarRepository.GetActiveDescriptionAsync(cancellationToken);

        return new SlaConfigurationDto(
            policies.Select(p => new SlaPolicyDto(
                p.PriorityId, PriorityLabel(p.PriorityId), p.FirstResponseTargetMinutes, p.ResolutionTargetMinutes,
                ClockBasisLabel(p.ClockBasis), p.WarningThresholdPercent, p.IsActive)).ToList(),
            calendar,
            ClockStartRule,
            PolicyPrecedence,
            FirstResponseRule,
            PauseRule);
    }

    /// <summary>
    /// Why the ticket's SLA is what it is — derived from the ticket, its
    /// current period and the policy the period was (or would be) computed
    /// from. A ticket with no period gets the precise reason the clock has not
    /// started, in the order the code itself checks: no classification/priority
    /// first (the Genesys case), then CRM reconciliation, then a missing policy.
    /// </summary>
    public static SlaExplanationDto BuildExplanation(
        Ticket ticket, TicketSlaInstance? instance, SlaPolicy? policy, SlaCalendarDto? calendar,
        RequestTypeSlaEvaluation? liveRequestTypeSla = null, SlaPauseSummary? pause = null)
    {
        var appliedPriority = instance?.PriorityId ?? ticket.PriorityId;
        var notes = new List<string>();
        string? notStarted = null;

        // Request-type SLA: what governed the period (the period's own
        // snapshot), or — before any period exists — what would.
        var requestTypeApplied = instance?.RequestTypeSlaPolicyId is not null;
        var requestTypeNote = instance is not null ? instance.RequestTypeSlaNote : liveRequestTypeSla?.Reason;
        var effectiveBasis = instance?.ResolutionClockBasis ?? policy?.ClockBasis;

        if (instance is null)
        {
            if (!ticket.IsClassified || ticket.PriorityId is null)
            {
                notStarted =
                    "No SLA is running: this ticket is unclassified (no category and/or no priority). "
                    + "Tickets created from Genesys calls, chats and messages start unclassified. "
                    + "The SLA starts when an agent classifies the ticket and sets its priority; the clock then starts at the classification time, not at the time of the interaction.";
            }
            else if (ticket.VerificationStatus == CrmVerificationStatus.PendingCrmVerification)
            {
                notStarted =
                    "No SLA is running: the ticket is provisional, awaiting CRM reconciliation. The clock starts when it is reconciled.";
            }
            else if (policy is null)
            {
                notStarted =
                    $"No SLA is running: no active SLA policy is configured for priority {PriorityLabel(appliedPriority)}.";
            }
            else
            {
                notStarted = "No SLA period has been opened for this ticket.";
            }
        }
        else
        {
            notes.Add(instance.ChangeReason == SlaChangeReason.Reopen
                ? "This is a reopen cycle: the Resolution target was recomputed from the reopen time; First Response is carried over from the original period and never restarts."
                : "Due times were computed once, when the clock started, from the policy's targets and clock basis.");

            if (ticket.SlaState == SlaState.Breached)
            {
                notes.Add(instance.FirstResponseBreached && instance.ResolutionBreached
                    ? "Both the First Response and the Resolution deadlines were missed."
                    : instance.FirstResponseBreached
                        ? "The First Response deadline was missed."
                        : "The Resolution deadline was missed.");
            }
        }

        if (policy is not null && effectiveBasis == SlaClockBasis.BusinessHours)
        {
            notes.Add(calendar is null
                ? "Clock basis is business hours, but no active business calendar is configured — the calculation cannot run without one."
                : $"Business-hours clock: only {calendar.BusinessDayStartLocal}–{calendar.BusinessDayEndLocal} ({calendar.TimeZoneId}) on {string.Join(", ", calendar.WorkingDays)} count; holidays are skipped.");
        }
        else if (policy is not null)
        {
            notes.Add("24/7 clock: every minute counts, including nights, weekends and holidays.");
        }

        if (requestTypeApplied)
        {
            notes.Add(instance!.AppliedResolutionTargetMinutes == 0
                ? "Request-type SLA applied: this request type is resolved immediately, so the Resolution deadline is the clock start."
                : $"Request-type SLA applied: the Resolution target ({FormatMinutes(instance.AppliedResolutionTargetMinutes)}) comes from this request type's own SLA on a "
                  + $"{(effectiveBasis == SlaClockBasis.BusinessHours ? "business-hours" : "calendar (24/7)")} clock.");
        }
        else if (requestTypeNote is not null)
        {
            // The exact reason, verbatim (e.g. "Request-type SLA not applied: range interpretation undecided …").
            notes.Add(requestTypeNote);
        }

        if (pause is { IsCurrentlyPaused: true })
        {
            notes.Add($"The Resolution SLA is paused ({pause.CurrentPauseReason}); its deadline is held and will be extended by the paused time on resume. First Response is not paused.");
        }

        if (pause is { TotalPausedMinutes: > 0 })
        {
            notes.Add($"Total paused time in this period: {FormatMinutes(pause.TotalPausedMinutes)}.");
        }

        return new SlaExplanationDto(
            HasActivePeriod: instance is not null,
            AppliedPriorityId: appliedPriority,
            AppliedPriorityLabel: appliedPriority is { } p ? PriorityLabel(p) : null,
            PolicySource: requestTypeApplied ? PolicySourceRequestType : PolicySourcePriority,
            FirstResponseTargetMinutes: instance?.AppliedFirstResponseTargetMinutes ?? policy?.FirstResponseTargetMinutes,
            ResolutionTargetMinutes: instance?.AppliedResolutionTargetMinutes ?? policy?.ResolutionTargetMinutes,
            ClockBasis: effectiveBasis is { } basis ? ClockBasisLabel(basis) : null,
            WarningThresholdPercent: policy?.WarningThresholdPercent,
            Calendar: calendar,
            ClockStartedAtUtc: instance?.PeriodStartAtUtc,
            PeriodReason: instance?.ChangeReason.ToString(),
            NotStartedReason: notStarted,
            FirstResponseRule: FirstResponseRule,
            PauseRule: PauseRule,
            Notes: notes,
            RequestTypeSlaApplied: requestTypeApplied,
            RequestTypeSlaNote: requestTypeNote);
    }

    private static string FormatMinutes(int? minutes) => minutes switch
    {
        null => "unknown",
        < 60 => $"{minutes} min",
        _ when minutes % 60 == 0 => $"{minutes / 60} h",
        _ => $"{minutes / 60} h {minutes % 60} min"
    };

    internal static string PriorityLabel(byte? priorityId) => priorityId switch
    {
        1 => "Critical",
        2 => "High",
        3 => "Medium",
        4 => "Low",
        null => "none",
        _ => $"Priority {priorityId}"
    };

    internal static string ClockBasisLabel(SlaClockBasis basis) =>
        basis == SlaClockBasis.BusinessHours ? "Business hours" : "24/7";

    /// <summary>The §5.1 response shape, built once here so every endpoint that returns an SLA summary returns the identical projection.</summary>
    internal static TicketSlaSummaryResponseDto ToSummaryDto(Ticket ticket, TicketSlaInstance? instance, SlaPauseSummary? pause = null) =>
        new(SlaState: ticket.SlaState.ToString(),
            FirstResponseDueAtUtc: instance?.FirstResponseDueAtUtc,
            FirstResponseBreached: instance?.FirstResponseBreached ?? false,
            FirstHumanResponseAtUtc: ticket.FirstHumanResponseAtUtc,
            ResolutionDueAtUtc: instance?.ResolutionDueAtUtc,
            ResolutionBreached: instance?.ResolutionBreached ?? false,

            // ISSUE-018 pause facts of the current period. A response built
            // without them (a caller that has no pause repository, or a
            // ticket with no period) reports the pause-free values.
            IsCurrentlyPaused: pause?.IsCurrentlyPaused ?? false,
            CurrentPauseReason: pause?.CurrentPauseReason,
            TotalPausedMinutesThisPeriod: pause?.TotalPausedMinutes ?? 0,

            EscalationLevel: ticket.EscalationLevel.ToString());

    /// <summary>MVP-API-Contracts.md §5.9 — escalation history for a ticket, most recent first.</summary>
    public async Task<SlaOperationResult<IReadOnlyList<TicketEscalationResponseDto>>> ListEscalationsAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return SlaOperationResult<IReadOnlyList<TicketEscalationResponseDto>>.Failure(SlaOperationOutcome.NotFound);
        }

        if (!await ticketQueryAppService.CanViewDepartmentAsync(
                callerEmployeeId, callerRoles, ticket.CurrentDepartmentId, cancellationToken))
        {
            return SlaOperationResult<IReadOnlyList<TicketEscalationResponseDto>>.Failure(SlaOperationOutcome.Forbidden);
        }

        var escalations = await escalationRepository.ListByTicketIdAsync(ticketId, cancellationToken);

        return SlaOperationResult<IReadOnlyList<TicketEscalationResponseDto>>.Success(
            escalations.Select(ToDto).ToList());
    }

    internal static TicketEscalationResponseDto ToDto(TicketEscalation escalation) =>
        new(escalation.TicketEscalationId,
            escalation.TicketId,
            escalation.Level,
            escalation.TriggerType.ToString(),
            escalation.NotifiedRoles,
            escalation.RaisedAtUtc,
            escalation.RespondedAtUtc,
            escalation.RespondingEmployeeId);
}

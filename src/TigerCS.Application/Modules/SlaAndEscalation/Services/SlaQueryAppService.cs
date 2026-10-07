using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;
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
    IBusinessCalendarRepository businessCalendarRepository)
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

    public const string PolicyPrecedence =
        "Targets come from the per-priority SLA policy (Critical, High, Medium, Low). "
        + "The SLA values configured per request type are stored and shown for reference but are not applied by the due-date calculation in this release; "
        + "no department-level SLA exists.";

    public const string FirstResponseRule =
        "First response is satisfied only by a recorded human response: a human agent's message in the Genesys conversation transcript, or an explicit 'record first response' call. "
        + "Accepting a pending interaction (Accept & Start / Start Handling), assigning the ticket, changing its status or the automated acknowledgement do NOT count.";

    public const string PauseRule =
        "Pause and resume are not implemented in this release: the clock never pauses, including while the ticket is Pending Customer. "
        + "The request-type 'pauses on Pending Customer' setting is stored but not applied.";

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
        var calendar = policy is { ClockBasis: SlaClockBasis.BusinessHours }
            ? await businessCalendarRepository.GetActiveDescriptionAsync(cancellationToken)
            : null;

        return SlaOperationResult<TicketSlaSummaryResponseDto>.Success(
            ToSummaryDto(ticket, instance) with { Explanation = BuildExplanation(ticket, instance, policy, calendar) });
    }

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
        Ticket ticket, TicketSlaInstance? instance, SlaPolicy? policy, SlaCalendarDto? calendar)
    {
        var appliedPriority = instance?.PriorityId ?? ticket.PriorityId;
        var notes = new List<string>();
        string? notStarted = null;

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

        if (policy is not null && policy.ClockBasis == SlaClockBasis.BusinessHours)
        {
            notes.Add(calendar is null
                ? "Clock basis is business hours, but no active business calendar is configured — the calculation cannot run without one."
                : $"Business-hours clock: only {calendar.BusinessDayStartLocal}–{calendar.BusinessDayEndLocal} ({calendar.TimeZoneId}) on {string.Join(", ", calendar.WorkingDays)} count; holidays are skipped.");
        }
        else if (policy is not null)
        {
            notes.Add("24/7 clock: every minute counts, including nights, weekends and holidays.");
        }

        return new SlaExplanationDto(
            HasActivePeriod: instance is not null,
            AppliedPriorityId: appliedPriority,
            AppliedPriorityLabel: appliedPriority is { } p ? PriorityLabel(p) : null,
            PolicySource: PolicySourcePriority,
            FirstResponseTargetMinutes: policy?.FirstResponseTargetMinutes,
            ResolutionTargetMinutes: policy?.ResolutionTargetMinutes,
            ClockBasis: policy is null ? null : ClockBasisLabel(policy.ClockBasis),
            WarningThresholdPercent: policy?.WarningThresholdPercent,
            Calendar: calendar,
            ClockStartedAtUtc: instance?.PeriodStartAtUtc,
            PeriodReason: instance?.ChangeReason.ToString(),
            NotStartedReason: notStarted,
            FirstResponseRule: FirstResponseRule,
            PauseRule: PauseRule,
            Notes: notes);
    }

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
    internal static TicketSlaSummaryResponseDto ToSummaryDto(Ticket ticket, TicketSlaInstance? instance) =>
        new(SlaState: ticket.SlaState.ToString(),
            FirstResponseDueAtUtc: instance?.FirstResponseDueAtUtc,
            FirstResponseBreached: instance?.FirstResponseBreached ?? false,
            FirstHumanResponseAtUtc: ticket.FirstHumanResponseAtUtc,
            ResolutionDueAtUtc: instance?.ResolutionDueAtUtc,
            ResolutionBreached: instance?.ResolutionBreached ?? false,

            // MVP-Implementation-Backlog.md §0.2 — SLA pause/resume is not
            // built in this pilot. The three fields stay in the approved §5.1
            // response shape at their pause-free values rather than being
            // dropped from the contract.
            IsCurrentlyPaused: false,
            CurrentPauseReason: null,
            TotalPausedMinutesThisPeriod: 0,

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

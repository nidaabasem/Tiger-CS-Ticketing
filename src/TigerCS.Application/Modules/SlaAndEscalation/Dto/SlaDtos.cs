namespace TigerCS.Application.Modules.SlaAndEscalation.Dto;

// ---- SLA summary (MVP-API-Contracts.md §5.1) ----

/// <summary>
/// The SLA panel for one ticket (MVP-API-Contracts.md §5.1).
///
/// <para>
/// The three pause fields (§5.1) report the Resolution clock's pause state
/// from <c>TicketSlaPausePeriods</c> (ISSUE-018): whether it is paused right
/// now, why, and the total paused time of the current period (an open pause
/// counted to "now"). First Response never pauses.
/// </para>
/// </summary>
/// <param name="SlaState">The ticket-level summary of both clocks: Running, Paused, Met, Breached, or NotApplicable.</param>
/// <param name="FirstResponseDueAtUtc">When the First Response target expires. Null only while a provisional ticket is still awaiting CRM reconciliation, since an unverified ticket has no running clock (FR-TKT-09).</param>
/// <param name="FirstResponseBreached">True once the First Response deadline was missed. Never returns to false.</param>
/// <param name="FirstHumanResponseAtUtc">The moment the First Response SLA was satisfied, if it has been. Never set by the automated acknowledgement (ISSUE-019).</param>
/// <param name="ResolutionDueAtUtc">When the Resolution target expires. Null under the same condition as <paramref name="FirstResponseDueAtUtc"/>.</param>
/// <param name="ResolutionBreached">True once the Resolution deadline was missed. Never returns to false.</param>
/// <param name="IsCurrentlyPaused">True while the Resolution clock is paused (Pending Customer, non-Critical).</param>
/// <param name="CurrentPauseReason">Why it is paused ("Pending Customer", "Pending Third Party (legacy)"), or null when running.</param>
/// <param name="TotalPausedMinutesThisPeriod">Whole minutes the Resolution clock has been paused in the current SLA period.</param>
/// <param name="EscalationLevel">The ticket's current escalation level — an independent dimension, never derived from and never changing TicketStatus (ADR-0008/ADR-0011).</param>
/// <param name="Explanation">How the effective SLA was arrived at — or why there is none yet. Additive to the approved §5.1 shape; the fields above are unchanged.</param>
public sealed record TicketSlaSummaryResponseDto(
    string SlaState,
    DateTime? FirstResponseDueAtUtc,
    bool FirstResponseBreached,
    DateTime? FirstHumanResponseAtUtc,
    DateTime? ResolutionDueAtUtc,
    bool ResolutionBreached,
    bool IsCurrentlyPaused,
    string? CurrentPauseReason,
    int TotalPausedMinutesThisPeriod,
    string EscalationLevel,
    SlaExplanationDto? Explanation = null);

/// <summary>
/// The effective SLA of one ticket, made legible: which policy applies and
/// why, what the targets are, on which clock, from when, and — when no SLA
/// is running — exactly why not. Every statement here is derived from what
/// the calculation actually does today (<c>SlaDueDateService</c>,
/// <c>TicketClassificationAppService</c>, <c>FirstHumanResponseRecorder</c>);
/// nothing is re-computed or re-decided in this projection.
/// </summary>
/// <param name="HasActivePeriod">True when an SLA period is open for the ticket (the clock has started).</param>
/// <param name="AppliedPriorityId">The priority the period was computed from (the period's own priority when one exists, else the ticket's).</param>
/// <param name="AppliedPriorityLabel">Critical / High / Medium / Low, or null when the ticket has no priority.</param>
/// <param name="PolicySource">Which configuration layer decided the targets. In this release: the per-priority SLA policy.</param>
/// <param name="FirstResponseTargetMinutes">The First Response target of the applied policy, in minutes.</param>
/// <param name="ResolutionTargetMinutes">The Resolution target of the applied policy, in minutes.</param>
/// <param name="ClockBasis">"24/7" or "Business hours".</param>
/// <param name="WarningThresholdPercent">Share of the target after which the ticket counts as at risk.</param>
/// <param name="Calendar">The business calendar in force (working hours, zone, working days, holidays) — only when the basis is business hours.</param>
/// <param name="ClockStartedAtUtc">When the current period's clock started (the period start), or null when none.</param>
/// <param name="PeriodReason">Why the current period exists: InitialCreation, Upgrade, Downgrade or Reopen.</param>
/// <param name="NotStartedReason">Plain-language reason there is no running SLA, or null when there is one.</param>
/// <param name="FirstResponseRule">What counts as the first human response — and what does not.</param>
/// <param name="PauseRule">How pausing works in this release.</param>
/// <param name="Notes">Further statements about how the figures were arrived at.</param>
/// <param name="RequestTypeSlaApplied">True when the request type's own SLA governed the Resolution (and possibly First Response) targets of this period.</param>
/// <param name="RequestTypeSlaNote">When a request-type SLA is configured but was NOT applied: the exact reason (e.g. "Request-type SLA not applied: Resolution is a range (10–12 Days) and the range interpretation … is undecided."). Null otherwise.</param>
public sealed record SlaExplanationDto(
    bool HasActivePeriod,
    byte? AppliedPriorityId,
    string? AppliedPriorityLabel,
    string PolicySource,
    int? FirstResponseTargetMinutes,
    int? ResolutionTargetMinutes,
    string? ClockBasis,
    decimal? WarningThresholdPercent,
    SlaCalendarDto? Calendar,
    DateTime? ClockStartedAtUtc,
    string? PeriodReason,
    string? NotStartedReason,
    string FirstResponseRule,
    string PauseRule,
    IReadOnlyList<string> Notes,
    bool RequestTypeSlaApplied = false,
    string? RequestTypeSlaNote = null);

/// <summary>The active business calendar, as configuration reference data (read-only in this release).</summary>
/// <param name="Name">The calendar's name.</param>
/// <param name="BusinessDayStartLocal">Start of the working day, local time ("08:00").</param>
/// <param name="BusinessDayEndLocal">End of the working day, local time ("18:00").</param>
/// <param name="TimeZoneId">IANA zone id, e.g. Asia/Dubai.</param>
/// <param name="WorkingDays">The working days of the week, in week order.</param>
/// <param name="HolidayCount">How many holiday dates are configured.</param>
/// <param name="UpcomingHolidays">The next configured holiday dates (at most ten), for the reader's orientation.</param>
public sealed record SlaCalendarDto(
    string Name,
    string BusinessDayStartLocal,
    string BusinessDayEndLocal,
    string TimeZoneId,
    IReadOnlyList<string> WorkingDays,
    int HolidayCount,
    IReadOnlyList<DateOnly> UpcomingHolidays);

/// <summary>One per-priority SLA policy row — the layer the calculation applies.</summary>
/// <param name="PriorityId">1 Critical … 4 Low.</param>
/// <param name="PriorityLabel">Critical / High / Medium / Low.</param>
/// <param name="FirstResponseTargetMinutes">First Response target, in minutes.</param>
/// <param name="ResolutionTargetMinutes">Resolution target, in minutes.</param>
/// <param name="ClockBasis">"24/7" or "Business hours".</param>
/// <param name="WarningThresholdPercent">At-risk threshold.</param>
/// <param name="IsActive">Whether the row is in force.</param>
public sealed record SlaPolicyDto(
    byte PriorityId,
    string PriorityLabel,
    int FirstResponseTargetMinutes,
    int ResolutionTargetMinutes,
    string ClockBasis,
    decimal WarningThresholdPercent,
    bool IsActive);

/// <summary>
/// The SLA configuration the calculation actually applies (System
/// Administrator reference view): the per-priority policies, the active
/// business calendar and the implementation facts an administrator needs to
/// read the request-type SLA values correctly. Read-only — the values are
/// seeded/approved reference data and are not edited from this release's UI.
/// </summary>
/// <param name="Policies">The per-priority policies, Critical first.</param>
/// <param name="Calendar">The active business calendar, or null when none is configured.</param>
/// <param name="ClockStartRule">When a ticket's SLA clock starts.</param>
/// <param name="PolicyPrecedence">Which configuration layer the calculation uses today.</param>
/// <param name="FirstResponseRule">What counts as the first human response.</param>
/// <param name="PauseRule">How pausing works in this release.</param>
public sealed record SlaConfigurationDto(
    IReadOnlyList<SlaPolicyDto> Policies,
    SlaCalendarDto? Calendar,
    string ClockStartRule,
    string PolicyPrecedence,
    string FirstResponseRule,
    string PauseRule);

// ---- First response (MVP-API-Contracts.md §5.2) ----

/// <summary>Record that the First Human Response has occurred (MVP-API-Contracts.md §5.2, ISSUE-019).</summary>
/// <param name="Source">Required. <c>Manual</c> (an agent recording live handling) or <c>GenesysCallAnswer</c>. No Genesys adapter ships in this increment; the value is accepted because it is part of the approved DTO.</param>
/// <param name="OccurredAtUtc">Optional; defaults to server "now". May legitimately precede the ticket's own creation timestamp (SLA-Architecture.md §16 Example E).</param>
/// <param name="RowVersion">Required. The <c>rowVersion</c> from the ticket you read. A stale value is answered with 409.</param>
public sealed record RecordFirstResponseRequestDto(string Source, DateTime? OccurredAtUtc, byte[] RowVersion);

// ---- Escalation (MVP-API-Contracts.md §5.7/§5.9) ----

/// <summary>Manually escalate a ticket (MVP-API-Contracts.md §5.7, FR-ESC-01).</summary>
/// <param name="Level">Required. 1–4. Level 4 is only reachable with <paramref name="TriggerType"/> <c>ManualLevel4</c>, which is restricted to CS Manager/GM.</param>
/// <param name="TriggerType">Required. <c>ManualFlag</c> or <c>ManualLevel4</c>. The automatic trigger types are system-only and are rejected here.</param>
/// <param name="Note">Optional free-text note, recorded on the audit entry.</param>
/// <param name="RowVersion">Required. The <c>rowVersion</c> from the ticket you read. A stale value is answered with 409.</param>
public sealed record ManualEscalationRequestDto(byte Level, string TriggerType, string? Note, byte[] RowVersion);

/// <summary>One escalation event (MVP-Data-Dictionary.md §2.17).</summary>
/// <param name="TicketEscalationId">Identifier of the escalation row.</param>
/// <param name="TicketId">The escalated ticket.</param>
/// <param name="Level">The escalation level this event raised the ticket to.</param>
/// <param name="TriggerType">AutoBreach, AutoWindowExpired, ManualFlag, or ManualLevel4.</param>
/// <param name="NotifiedRoles">Who the approved recipient matrix says should be told — deliberately distinct from <paramref name="Level"/> (ADR-0011). Notification delivery itself is out of this pilot's scope.</param>
/// <param name="RaisedAtUtc">When the escalation was raised.</param>
/// <param name="RespondedAtUtc">Always null in this pilot — the respond action (MVP-API-Contracts.md §5.8) does not ship in this increment.</param>
/// <param name="RespondingEmployeeId">Always null in this pilot — see <paramref name="RespondedAtUtc"/>.</param>
public sealed record TicketEscalationResponseDto(
    long TicketEscalationId,
    long TicketId,
    byte Level,
    string TriggerType,
    string? NotifiedRoles,
    DateTime RaisedAtUtc,
    DateTime? RespondedAtUtc,
    Guid? RespondingEmployeeId);

// ---- Outcomes ----

/// <summary>Outcomes of the SLA and escalation actions, mapped to HTTP status codes by the controller.</summary>
public enum SlaOperationOutcome
{
    Success,
    NotFound,
    Forbidden,
    ConcurrencyConflict,

    /// <summary>Closed-ticket immutability: a Closed ticket accepts no first-response recording and no further escalation.</summary>
    TicketClosed,

    /// <summary>MVP-API-Contracts.md §5.2: `409 first-response-already-recorded` — the field is write-once.</summary>
    FirstResponseAlreadyRecorded,

    /// <summary>The request body could not be interpreted (unparseable source, trigger type, or level).</summary>
    InvalidRequest,

    /// <summary>The requested level is at or below the ticket's current escalation level — levels only rise (ADR-0011).</summary>
    EscalationLevelNotAnAdvance,

    /// <summary>Level 4 was requested without the <c>ManualLevel4</c> trigger type, which would bypass its CS Manager/GM role gate.</summary>
    EscalationLevelTriggerMismatch
}

public sealed record SlaOperationResult<T>(SlaOperationOutcome Outcome, T? Response = default)
{
    public static SlaOperationResult<T> Success(T response) => new(SlaOperationOutcome.Success, response);
    public static SlaOperationResult<T> Failure(SlaOperationOutcome outcome) => new(outcome);
}

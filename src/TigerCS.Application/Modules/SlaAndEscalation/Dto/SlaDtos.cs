namespace TigerCS.Application.Modules.SlaAndEscalation.Dto;

// ---- SLA summary (MVP-API-Contracts.md §5.1) ----

/// <summary>
/// The SLA panel for one ticket (MVP-API-Contracts.md §5.1).
///
/// <para>
/// The three pause fields are part of §5.1's approved response shape and are
/// returned at their pause-free values in this pilot: SLA pause/resume
/// (<c>TicketSlaPausePeriods</c>) is explicitly not built
/// (MVP-Implementation-Backlog.md §0.2 — "a real, disclosed limitation, not a
/// silent one"). They are kept rather than dropped so the approved contract
/// does not change shape when the post-pilot feature lands.
/// </para>
/// </summary>
/// <param name="SlaState">The ticket-level summary of both clocks: Running, Paused, Met, Breached, or NotApplicable.</param>
/// <param name="FirstResponseDueAtUtc">When the First Response target expires. Null only while a provisional ticket is still awaiting CRM reconciliation, since an unverified ticket has no running clock (FR-TKT-09).</param>
/// <param name="FirstResponseBreached">True once the First Response deadline was missed. Never returns to false.</param>
/// <param name="FirstHumanResponseAtUtc">The moment the First Response SLA was satisfied, if it has been. Never set by the automated acknowledgement (ISSUE-019).</param>
/// <param name="ResolutionDueAtUtc">When the Resolution target expires. Null under the same condition as <paramref name="FirstResponseDueAtUtc"/>.</param>
/// <param name="ResolutionBreached">True once the Resolution deadline was missed. Never returns to false.</param>
/// <param name="IsCurrentlyPaused">Always false in this pilot — see this type's remarks.</param>
/// <param name="CurrentPauseReason">Always null in this pilot — see this type's remarks.</param>
/// <param name="TotalPausedMinutesThisPeriod">Always 0 in this pilot — see this type's remarks.</param>
/// <param name="EscalationLevel">The ticket's current escalation level — an independent dimension, never derived from and never changing TicketStatus (ADR-0008/ADR-0011).</param>
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
    string EscalationLevel);

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

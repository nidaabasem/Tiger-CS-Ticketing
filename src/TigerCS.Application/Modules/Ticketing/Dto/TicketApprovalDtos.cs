namespace TigerCS.Application.Modules.Ticketing.Dto;

// ---- Approvals / dependencies (Workflow/Automation phase 3) ----

/// <summary>Open a new approval cycle on a ticket.</summary>
/// <param name="ApprovalType">Required. One of the controlled approval types configured for the ticket's request type (e.g. AccountingApproval, CustomerServiceApproval).</param>
/// <param name="Comment">Optional request context (e.g. what needs approving).</param>
public sealed record RequestApprovalRequestDto(string ApprovalType, string? Comment = null);

/// <summary>Decide a pending approval cycle.</summary>
/// <param name="Decision">Required. "Approve" or "Reject".</param>
/// <param name="Comment">Optional on approval; REQUIRED on rejection (a rejection always carries its why).</param>
public sealed record DecideApprovalRequestDto(string Decision, string? Comment = null);

/// <summary>Withdraw a still-pending approval cycle (e.g. raised in error). The record is kept as history.</summary>
/// <param name="Comment">Optional context for the cancellation.</param>
public sealed record CancelApprovalRequestDto(string? Comment = null);

/// <summary>Record a typed workflow event (PrerequisitesCompleted / MaintenanceRequired / MaintenanceNotRequired / MaintenanceCompleted). Approval events are never recorded through this — they come from approval actions.</summary>
/// <param name="EventType">Required. One of the recordable dependency event types.</param>
/// <param name="Note">Optional short context, display only.</param>
public sealed record RecordWorkflowEventRequestDto(string EventType, string? Note = null);

/// <summary>One approval cycle as shown on Ticket Details. Approval state is separate from TicketStatus by design.</summary>
/// <param name="TicketApprovalId">The cycle.</param>
/// <param name="ApprovalType">AccountingApproval / CustomerServiceApproval.</param>
/// <param name="Status">Pending / Approved / Rejected / Cancelled.</param>
/// <param name="TargetSummary">Human-readable "who decides" (e.g. "Accounting department", "CS Supervisor role") — never a raw technical id.</param>
/// <param name="RequestedByEmployeeId">Who opened the cycle.</param>
/// <param name="RequestedAtUtc">When it was opened.</param>
/// <param name="RequestComment">The request context, if any.</param>
/// <param name="DecidedByEmployeeId">Who decided, once decided.</param>
/// <param name="DecisionAtUtc">The decision moment — the timestamp phase 4's conditional SLA triggers read (via the typed workflow event).</param>
/// <param name="DecisionComment">The decision comment; on rejection, the mandatory reason.</param>
/// <param name="IsCurrent">Whether this is the latest cycle of its type.</param>
/// <param name="CallerCanDecide">Whether the calling user is an authorized approver for this cycle — the UI shows decision buttons only when true.</param>
public sealed record TicketApprovalDto(
    long TicketApprovalId,
    string ApprovalType,
    string Status,
    string TargetSummary,
    Guid RequestedByEmployeeId,
    DateTime RequestedAtUtc,
    string? RequestComment,
    Guid? DecidedByEmployeeId,
    DateTime? DecisionAtUtc,
    string? DecisionComment,
    bool IsCurrent,
    bool CallerCanDecide);

/// <summary>An approval the request type requires but that has no current cycle yet (or whose last cycle ended without approval), offered to authorized users as requestable.</summary>
/// <param name="ApprovalType">The configured approval type.</param>
/// <param name="TargetSummary">Human-readable "who decides".</param>
/// <param name="BlocksWorkUntilApproved">Whether operational work is expected to wait for it.</param>
/// <param name="CallerCanRequest">Whether the calling user may open the cycle.</param>
public sealed record RequestableApprovalDto(
    string ApprovalType,
    string TargetSummary,
    bool BlocksWorkUntilApproved,
    bool CallerCanRequest);

/// <summary>One typed workflow event, for the dependency timeline.</summary>
public sealed record TicketWorkflowEventDto(
    string EventType, DateTime OccurredAtUtc, Guid? ActorEmployeeId, string? Note);

/// <summary>
/// The Approvals / Dependencies section of Ticket Details: approval cycles
/// (full history), still-requestable requirements, and the derived
/// dependency states. Dependency states are display summaries derived from
/// typed events — "MaintenanceState": null (not recorded), "Required",
/// "NotRequired", "Completed"; "PrerequisitesCompletedAtUtc": null until
/// explicitly recorded.
/// </summary>
public sealed record TicketApprovalsViewDto(
    IReadOnlyList<TicketApprovalDto> Approvals,
    IReadOnlyList<RequestableApprovalDto> RequestableApprovals,
    IReadOnlyList<TicketWorkflowEventDto> Events,
    string? MaintenanceState,
    DateTime? PrerequisitesCompletedAtUtc,
    bool CallerCanRecordEvents);

public enum ApprovalMutationOutcome
{
    Success,
    TicketNotFound,
    TicketClosed,
    Forbidden,

    /// <summary>The ticket's request type has no active requirement of this approval type — approvals are configuration-driven, never ad hoc.</summary>
    ApprovalNotConfigured,

    /// <summary>An active (Pending) cycle of this type already exists — no duplicate simultaneous approvals.</summary>
    DuplicateActiveApproval,

    ApprovalNotFound,

    /// <summary>The cycle is already decided — decisions are write-once; open a new cycle instead.</summary>
    ApprovalAlreadyDecided,

    /// <summary>A rejection requires a reason.</summary>
    ReasonRequired,

    /// <summary>The approval type / decision / event type did not parse to a supported value.</summary>
    InvalidInput,

    /// <summary>This event was already recorded and is not repeatable (e.g. a second PrerequisitesCompleted).</summary>
    EventAlreadyRecorded,

    /// <summary>The event does not apply in the ticket's current dependency state (e.g. MaintenanceCompleted with no maintenance required).</summary>
    EventNotApplicable
}

public sealed record ApprovalMutationResult(ApprovalMutationOutcome Outcome, TicketApprovalDto? Response = null)
{
    public static ApprovalMutationResult Success(TicketApprovalDto? response = null) =>
        new(ApprovalMutationOutcome.Success, response);

    public static ApprovalMutationResult Failure(ApprovalMutationOutcome outcome) => new(outcome);
}

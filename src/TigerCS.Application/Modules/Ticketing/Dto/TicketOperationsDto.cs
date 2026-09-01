using TigerCS.Application.Modules.Ticketing.Abstractions;

namespace TigerCS.Application.Modules.Ticketing.Dto;

// ---- Queries (MVP-API-Contracts.md §3.2/§3.3) ----

/// <summary>Filtering, sorting, and paging for the ticket queue (MVP-API-Contracts.md §3.2). Bound from the query string; every filter is optional and combines with AND.</summary>
/// <param name="DepartmentId">Narrow to one department. Cannot widen what the caller may already see.</param>
/// <param name="CategoryId">Narrow to one category.</param>
/// <param name="PriorityId">Narrow to one priority. 1=Critical, 2=High, 3=Medium, 4=Low.</param>
/// <param name="TicketStatus">Narrow to one status: Open, InProgress, PendingCustomer, PendingThirdParty, Resolved, Closed.</param>
/// <param name="VerificationStatus">Narrow to one verification status: Unverified, PendingCrmVerification, Verified.</param>
/// <param name="OwnerEmployeeId">Narrow to tickets currently owned by one employee.</param>
/// <param name="Search">Free-text search over the ticket number and request summary.</param>
/// <param name="SortBy">Which field to sort by.</param>
/// <param name="SortDir">Sort direction — asc or desc.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Page size.</param>
public sealed record TicketListRequestDto(
    int? DepartmentId,
    int? CategoryId,
    byte? PriorityId,
    string? TicketStatus,
    string? VerificationStatus,
    Guid? OwnerEmployeeId,
    string? Search,
    string? SortBy,
    string? SortDir,
    int Page,
    int PageSize);

/// <summary>One row of the ticket queue (MVP-API-Contracts.md §3.2).</summary>
/// <param name="TicketId">The ticket.</param>
/// <param name="TicketNumber">The human-facing ticket number.</param>
/// <param name="CurrentDepartmentId">The department that currently holds it.</param>
/// <param name="CurrentOwnerEmployeeId">The current owner, or null when unassigned.</param>
/// <param name="CategoryId">The ticket's category.</param>
/// <param name="PriorityId">1=Critical, 2=High, 3=Medium, 4=Low.</param>
/// <param name="TicketStatus">One of Open, InProgress, PendingCustomer, PendingThirdParty, Resolved, Closed.</param>
/// <param name="VerificationStatus">One of Unverified, PendingCrmVerification, Verified.</param>
/// <param name="RequestSummary">The request, in the agent's words.</param>
/// <param name="CreatedAtUtc">When the ticket was created, in UTC.</param>
public sealed record TicketSummaryDto(
    long TicketId,
    string TicketNumber,
    int CurrentDepartmentId,
    Guid? CurrentOwnerEmployeeId,
    int CategoryId,
    byte PriorityId,
    string TicketStatus,
    string VerificationStatus,
    string RequestSummary,
    DateTime CreatedAtUtc);

/// <summary>One page of the ticket queue.</summary>
/// <param name="Items">This page's tickets.</param>
/// <param name="TotalCount">How many tickets match the filters in total.</param>
/// <param name="Page">The 1-based page number that was served.</param>
/// <param name="PageSize">The page size that was served.</param>
public sealed record TicketListResultDto(IReadOnlyList<TicketSummaryDto> Items, int TotalCount, int Page, int PageSize);

/// <summary>Full ticket detail (MVP-API-Contracts.md §3.3), and the response of every ticket write.</summary>
/// <param name="TicketId">The ticket.</param>
/// <param name="TicketNumber">The human-facing ticket number.</param>
/// <param name="OriginatingDepartmentId">The department the ticket was raised in.</param>
/// <param name="CurrentDepartmentId">The department that currently holds it.</param>
/// <param name="CurrentOwnerEmployeeId">The current owner, or null when unassigned — including immediately after a transfer.</param>
/// <param name="UnitReferenceId">The verified unit, or null while the ticket is provisional.</param>
/// <param name="ContactReferenceId">The verified contact, or null while the ticket is provisional.</param>
/// <param name="CategoryId">The ticket's category.</param>
/// <param name="PriorityId">1=Critical, 2=High, 3=Medium, 4=Low.</param>
/// <param name="TicketStatus">One of Open, InProgress, PendingCustomer, PendingThirdParty, Resolved, Closed.</param>
/// <param name="VerificationStatus">One of Unverified, PendingCrmVerification, Verified.</param>
/// <param name="EscalationLevel">One of None, Level1, Level2, Level3, Level4.</param>
/// <param name="SlaState">One of Running, Paused, Met, Breached, NotApplicable.</param>
/// <param name="ResolutionOutcome">Set once the ticket is resolved. 1=Resolved, 2=Cancelled, 3=Rejected, 4=Duplicate.</param>
/// <param name="DuplicateOfTicketId">The ticket this one duplicates, when the outcome is Duplicate.</param>
/// <param name="RequestSummary">The request, in the agent's words.</param>
/// <param name="ReopenCount">How many times the ticket has been reopened.</param>
/// <param name="CreatedAtUtc">When the ticket was created, in UTC.</param>
/// <param name="RowVersion">The concurrency token, Base64-encoded. Send it back on the next write to this ticket.</param>
/// <param name="CrmBuyerCustomerId">The real CRM Buyer Lookup match's customer id, or null when no CRM Buyer match was linked at creation.</param>
/// <param name="CrmBuyerLeadId">The matched CRM Lead id, or null.</param>
/// <param name="CrmBuyerUnitId">The matched CRM unit id, or null.</param>
/// <param name="CrmBuyerProjectId">The matched CRM project id, or null.</param>
/// <param name="CrmBuyerCustomerName">Ticket-time display snapshot of the matched Buyer's name, or null.</param>
/// <param name="CrmBuyerProjectName">Ticket-time display snapshot of the matched unit's project name, or null.</param>
/// <param name="CrmBuyerUnitNumber">Ticket-time display snapshot of the matched unit's number, or null.</param>
/// <param name="ManualProjectName">The agent-entered Project name, when no CRM Buyer match was linked at creation.</param>
/// <param name="ManualUnitNumber">The agent-entered Unit Number, when no CRM Buyer match was linked at creation.</param>
/// <param name="CustomerVerificationSource">The external lookup source that verified the customer at creation ("Pact"/"Tasleeh"), or null for CRM Buyer tickets and plain manual entry.</param>
/// <param name="ExternalCustomerId">The source's own customer identifier (for PACT, its tenantID) — an external identifier only, never a local reference.</param>
/// <param name="ExternalUnitId">The source's own identifier for the selected unit (for PACT, its unitID) — an external identifier only.</param>
public sealed record TicketDetailDto(
    long TicketId,
    string TicketNumber,
    int OriginatingDepartmentId,
    int CurrentDepartmentId,
    Guid? CurrentOwnerEmployeeId,
    int? UnitReferenceId,
    int? ContactReferenceId,
    int CategoryId,
    byte PriorityId,
    string TicketStatus,
    string VerificationStatus,
    string EscalationLevel,
    string SlaState,
    byte? ResolutionOutcome,
    long? DuplicateOfTicketId,
    string RequestSummary,
    int ReopenCount,
    DateTime CreatedAtUtc,
    string RowVersion,
    int? CrmBuyerCustomerId = null,
    int? CrmBuyerLeadId = null,
    int? CrmBuyerUnitId = null,
    int? CrmBuyerProjectId = null,
    string? CrmBuyerCustomerName = null,
    string? CrmBuyerProjectName = null,
    string? CrmBuyerUnitNumber = null,
    string? ManualProjectName = null,
    string? ManualUnitNumber = null,
    string? CustomerVerificationSource = null,
    string? ExternalCustomerId = null,
    string? ExternalUnitId = null);

public enum TicketQueryOutcome
{
    Success,
    NotFound,
    Forbidden
}

public sealed record TicketQueryResultDto<T>(TicketQueryOutcome Outcome, T? Response = null) where T : class
{
    public static TicketQueryResultDto<T> Success(T response) => new(TicketQueryOutcome.Success, response);
    public static TicketQueryResultDto<T> Failure(TicketQueryOutcome outcome) => new(outcome);
}

// ---- Assignment / transfer (MVP-API-Contracts.md §3.5/§3.6) ----

/// <summary>Assign or reassign a ticket (MVP-API-Contracts.md §3.5).</summary>
/// <param name="AssignedEmployeeId">Required. Must be an active member of the ticket's current department.</param>
/// <param name="RowVersion">Required. The <c>rowVersion</c> from the ticket you read. A stale value is answered with 409.</param>
public sealed record AssignTicketRequestDto(Guid AssignedEmployeeId, byte[] RowVersion);

/// <summary>Transfer a ticket to another department (MVP-API-Contracts.md §3.6).</summary>
/// <param name="TargetDepartmentId">Required. Must be an active department, and not the ticket's current one.</param>
/// <param name="Reason">Required. Why the ticket is being transferred.</param>
/// <param name="RowVersion">Required. The <c>rowVersion</c> from the ticket you read. A stale value is answered with 409.</param>
public sealed record TransferTicketRequestDto(int TargetDepartmentId, string Reason, byte[] RowVersion);

public enum TicketMutationOutcome
{
    Success,
    NotFound,
    Forbidden,
    ConcurrencyConflict,

    /// <summary>Closed-ticket immutability (PR correction): Assign/Transfer/ChangeStatus/Resolve/Close all reject once TicketStatus is Closed — no database changes are made.</summary>
    TicketClosed,

    /// <summary>MVP-API-Contracts.md §3.5: AssignedEmployeeId is not an active member of the ticket's CurrentDepartmentId.</summary>
    EmployeeNotInDepartment,

    /// <summary>MVP-API-Contracts.md §3.6: target department doesn't exist or is inactive.</summary>
    TargetDepartmentInactive,

    /// <summary>MVP-API-Contracts.md §3.6: target equals the ticket's current department.</summary>
    AlreadyInTargetDepartment,

    InvalidStatusTransition,
    TicketNotAssigned,
    NotEligibleForResolution,
    NotYetResolved,
    DuplicateChainNotAllowed,

    /// <summary>Reconciliation-specific: the session's raw unit context doesn't match the ticket's originating raw unit number.</summary>
    ReconciliationUnitMismatch,

    /// <summary>Reconciliation-specific: the ticket is already Verified — there is no unit/contact left to reconcile.</summary>
    AlreadyVerified,

    VerificationSessionNotFound,
    VerificationSessionForbidden,
    VerificationSessionNotConfirmed,
    VerificationSessionAlreadyConsumed,
    VerificationSessionExpired
}

public sealed record TicketMutationResult(TicketMutationOutcome Outcome, TicketDetailDto? Response = null)
{
    public static TicketMutationResult Success(TicketDetailDto response) => new(TicketMutationOutcome.Success, response);
    public static TicketMutationResult Failure(TicketMutationOutcome outcome) => new(outcome);
}

// ---- Status / resolve / close (MVP-API-Contracts.md §3.7/§3.9/§3.10) ----

/// <summary>Move a ticket within the working sub-machine (MVP-API-Contracts.md §3.7).</summary>
/// <param name="NewStatus">Required. One of Open, InProgress, PendingCustomer, PendingThirdParty. Resolved and Closed are reached through their own endpoints.</param>
/// <param name="RowVersion">Required. The <c>rowVersion</c> from the ticket you read. A stale value is answered with 409.</param>
public sealed record ChangeStatusRequestDto(string NewStatus, byte[] RowVersion);

/// <summary>Resolve a ticket (MVP-API-Contracts.md §3.9).</summary>
/// <param name="ResolutionOutcome">Required. One of Resolved, Cancelled, Rejected, Duplicate.</param>
/// <param name="ResolutionNote">Required. How the ticket was resolved.</param>
/// <param name="ReasonCode">Optional reason code.</param>
/// <param name="DuplicateOfTicketId">The ticket this one duplicates. Required when the outcome is Duplicate; the target must exist and must not itself be a duplicate.</param>
/// <param name="RowVersion">Required. The <c>rowVersion</c> from the ticket you read. A stale value is answered with 409.</param>
public sealed record ResolveTicketRequestDto(
    string ResolutionOutcome, string ResolutionNote, byte? ReasonCode, long? DuplicateOfTicketId, byte[] RowVersion);

/// <summary>Close a resolved ticket (MVP-API-Contracts.md §3.10).</summary>
/// <param name="RowVersion">Required. The <c>rowVersion</c> from the ticket you read. A stale value is answered with 409.</param>
public sealed record CloseTicketRequestDto(byte[] RowVersion);

// ---- Notes (MVP-API-Contracts.md §4.1/§4.2) ----

/// <summary>Add a note to a ticket (MVP-API-Contracts.md §4.1).</summary>
/// <param name="NoteText">Required. The note's text.</param>
public sealed record CreateNoteRequestDto(string NoteText);

/// <summary>A ticket note (MVP-API-Contracts.md §4.1/§4.2).</summary>
/// <param name="TicketNoteId">The note.</param>
/// <param name="TicketId">The ticket it belongs to.</param>
/// <param name="NoteText">The note's text.</param>
/// <param name="AuthorEmployeeId">Who wrote it.</param>
/// <param name="CreatedAtUtc">When it was written, in UTC.</param>
public sealed record TicketNoteResponseDto(long TicketNoteId, long TicketId, string NoteText, Guid AuthorEmployeeId, DateTime CreatedAtUtc);

public enum NoteOutcome
{
    Success,
    TicketNotFound,
    Forbidden
}

public sealed record NoteResult(NoteOutcome Outcome, TicketNoteResponseDto? Response = null)
{
    public static NoteResult Success(TicketNoteResponseDto response) => new(NoteOutcome.Success, response);
    public static NoteResult Failure(NoteOutcome outcome) => new(outcome);
}

/// <summary>One page of a ticket's notes (MVP-API-Contracts.md §4.2).</summary>
/// <param name="Items">This page's notes.</param>
/// <param name="TotalCount">How many notes the ticket has in total.</param>
/// <param name="Page">The 1-based page number that was served.</param>
/// <param name="PageSize">The page size that was served.</param>
public sealed record TicketNoteListResultDto(IReadOnlyList<TicketNoteResponseDto> Items, int TotalCount, int Page, int PageSize);

// ---- CRM reconciliation (task item 6 — no reference API contract existed for this; designed this increment) ----

/// <summary>Reconcile a provisional ticket against a confirmed verification session.</summary>
/// <param name="VerificationSessionId">Required. A confirmed, unexpired, not-yet-consumed session owned by the caller, whose unit matches the ticket's originating raw unit number.</param>
/// <param name="RowVersion">Required. The <c>rowVersion</c> from the ticket you read. A stale value is answered with 409.</param>
public sealed record ReconcileTicketRequestDto(Guid VerificationSessionId, byte[] RowVersion);

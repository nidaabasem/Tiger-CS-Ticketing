using TigerCS.Application.Modules.Ticketing.Abstractions;

namespace TigerCS.Application.Modules.Ticketing.Dto;

// ---- Queries (MVP-API-Contracts.md §3.2/§3.3) ----

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

public sealed record TicketListResultDto(IReadOnlyList<TicketSummaryDto> Items, int TotalCount, int Page, int PageSize);

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
    string RowVersion);

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

public sealed record AssignTicketRequestDto(Guid AssignedEmployeeId, byte[] RowVersion);

public sealed record TransferTicketRequestDto(int TargetDepartmentId, string Reason, byte[] RowVersion);

public enum TicketMutationOutcome
{
    Success,
    NotFound,
    Forbidden,
    ConcurrencyConflict,

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

    /// <summary>Reconciliation-specific: VerificationStatus isn't PendingCrmVerification (already reconciled, or not a provisional ticket).</summary>
    NotPendingCrmVerification,

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

public sealed record ChangeStatusRequestDto(string NewStatus, byte[] RowVersion);

public sealed record ResolveTicketRequestDto(
    string ResolutionOutcome, string ResolutionNote, byte? ReasonCode, long? DuplicateOfTicketId, byte[] RowVersion);

public sealed record CloseTicketRequestDto(byte[] RowVersion);

// ---- Notes (MVP-API-Contracts.md §4.1/§4.2) ----

public sealed record CreateNoteRequestDto(string NoteText);

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

public sealed record TicketNoteListResultDto(IReadOnlyList<TicketNoteResponseDto> Items, int TotalCount, int Page, int PageSize);

// ---- CRM reconciliation (task item 6 — no reference API contract existed for this; designed this increment) ----

public sealed record ReconcileTicketRequestDto(Guid VerificationSessionId, byte[] RowVersion);

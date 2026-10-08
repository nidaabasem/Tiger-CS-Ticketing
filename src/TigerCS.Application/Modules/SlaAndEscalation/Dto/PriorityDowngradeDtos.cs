namespace TigerCS.Application.Modules.SlaAndEscalation.Dto;

/// <summary>MVP-API-Contracts.md section 5.6.1. Deliberately has no approver field of any kind (Finding DR-05).</summary>
/// <param name="NewPriorityId">Must be a genuine decrease (a numerically larger priority id).</param>
/// <param name="Reason">Required.</param>
public sealed record CreateDowngradeRequestRequestDto(byte NewPriorityId, string Reason);

/// <param name="RowVersion">The request's RowVersion (the If-Match of section 5.6.4); empty skips the optimistic check, the status check still applies.</param>
public sealed record ApproveDowngradeRequestRequestDto(byte[]? RowVersion = null);

/// <param name="DecisionNote">Required.</param>
/// <param name="RowVersion">The request's RowVersion; empty skips the optimistic check.</param>
public sealed record RejectDowngradeRequestRequestDto(string DecisionNote, byte[]? RowVersion = null);

/// <summary>A downgrade request as returned to clients. <c>Status</c> is the effective status: a Pending request past expiry reads Expired.</summary>
public sealed record PriorityDowngradeRequestResponseDto(
    long PriorityDowngradeRequestId,
    long TicketId,
    byte CurrentPriorityId,
    byte RequestedPriorityId,
    string Reason,
    string Status,
    Guid RequestedByEmployeeId,
    DateTime RequestedAtUtc,
    DateTime ExpiresAtUtc,
    Guid? DecidedByEmployeeId,
    DateTime? DecidedAtUtc,
    string? DecisionNote,
    byte[] RowVersion);

/// <summary>Section 5.6.4's success body: the decided request plus, on approval, the SLA period it opened.</summary>
public sealed record DowngradeDecisionResponseDto(
    PriorityDowngradeRequestResponseDto Request,
    TicketSlaPeriodDto? NewSlaPeriod);

public sealed record TicketSlaPeriodDto(
    long TicketSlaInstanceId,
    byte PriorityId,
    DateTime PeriodStartAtUtc,
    DateTime FirstResponseDueAtUtc,
    DateTime ResolutionDueAtUtc,
    bool FirstResponseBreached,
    bool ResolutionBreached,
    string ChangeReason,
    Guid? ApprovedByEmployeeId);

/// <summary>One inbox entry (section 5.6.3): enough ticket context to decide without a lookup.</summary>
public sealed record PendingDowngradeRequestDto(
    long PriorityDowngradeRequestId,
    long TicketId,
    string TicketNumber,
    int TicketDepartmentId,
    byte CurrentPriorityId,
    byte RequestedPriorityId,
    string Reason,
    Guid RequestedByEmployeeId,
    DateTime RequestedAtUtc,
    DateTime ExpiresAtUtc,
    byte[] RowVersion);

public sealed record PendingDowngradePageDto(
    IReadOnlyList<PendingDowngradeRequestDto> Items, int Page, int PageSize, int TotalCount);

/// <summary>Outcomes of the downgrade actions, mapped to HTTP by the controller.</summary>
public enum DowngradeOutcome
{
    Success,
    NotFound,
    Forbidden,

    /// <summary>400 - missing reason / decision note, or an unknown priority.</summary>
    InvalidRequest,

    /// <summary>422 - the requested priority is not a decrease (use the upgrade path).</summary>
    NotADowngrade,

    /// <summary>422 - the ticket has no priority yet (classify it first; that is not a downgrade).</summary>
    TicketNotClassified,

    /// <summary>422 - the ticket is Closed, or Resolved awaiting closure; its priority is final.</summary>
    TicketFinal,

    /// <summary>409 downgrade-request-already-pending - the response carries the existing request.</summary>
    AlreadyPending,

    /// <summary>409 downgrade-request-not-pending - already decided, expired or superseded.</summary>
    NotPending,

    /// <summary>410 - the request's ExpiresAtUtc has passed.</summary>
    Expired,

    /// <summary>409 - the ticket's priority is no longer the one the request was made against.</summary>
    StalePriority,

    /// <summary>403 downgrade-self-approval - the requester cannot decide their own request.</summary>
    SelfApprovalForbidden,

    /// <summary>409 - optimistic-concurrency / unique-index race lost.</summary>
    ConcurrencyConflict
}

public sealed record DowngradeResult<T>(
    DowngradeOutcome Outcome, T? Response = default, PriorityDowngradeRequestResponseDto? Existing = null)
{
    public static DowngradeResult<T> Success(T response) => new(DowngradeOutcome.Success, response);
    public static DowngradeResult<T> Failure(DowngradeOutcome outcome) => new(outcome);
}

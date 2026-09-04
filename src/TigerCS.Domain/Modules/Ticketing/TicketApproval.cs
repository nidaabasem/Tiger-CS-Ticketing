using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>How one approval cycle stands. A small dedicated state model — deliberately NOT <see cref="TicketStatus"/> values: approval state and ticket lifecycle are different concerns and stay in different records.</summary>
public enum ApprovalStatus : byte
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    Cancelled = 4
}

/// <summary>
/// One approval cycle of a ticket (Workflow/Automation phase 3) — the
/// authoritative approval state, independent of <see cref="TicketStatus"/>.
/// A ticket waiting on Accounting shows lifecycle
/// <see cref="TicketStatus.PendingThirdParty"/> (with its structured pending
/// reason) while THIS record is what actually says
/// "AccountingApproval: Pending".
///
/// <para>
/// <b>History is never overwritten.</b> A decision is write-once; a later
/// re-request (e.g. after a rejection) opens a NEW cycle and flips this
/// row's <see cref="IsCurrent"/> — the same append-plus-supersede pattern as
/// <c>TicketAssignment</c>/<c>TicketResolution</c>. At most one Pending
/// cycle per (ticket, approval type) exists at a time, guaranteed by a
/// filtered unique index.
/// </para>
///
/// <para>
/// The target columns are a snapshot of the requirement's target at request
/// time, so history stays truthful even if the configuration is later
/// re-pointed (e.g. Accounting moving from provisional department to an
/// approval role).
/// </para>
/// </summary>
public class TicketApproval
{
    public long TicketApprovalId { get; private set; }
    public long TicketId { get; private set; }
    public ApprovalType ApprovalType { get; private set; }
    public ApprovalStatus Status { get; private set; }

    // Target snapshot — who was asked, as configured when the request was made.
    public ApprovalTargetKind TargetKind { get; private set; }
    public int? TargetDepartmentId { get; private set; }
    public string? TargetRoleName { get; private set; }
    public Guid? TargetEmployeeId { get; private set; }

    public Guid RequestedByEmployeeId { get; private set; }
    public DateTime RequestedAtUtc { get; private set; }
    public string? RequestComment { get; private set; }

    public Guid? DecidedByEmployeeId { get; private set; }

    /// <summary>The decision moment — for an approved <see cref="ApprovalType.AccountingApproval"/>/<see cref="ApprovalType.CustomerServiceApproval"/> this is the timestamp phase 4's ApprovalReceived/CustomerServiceApproved SLA triggers read (via the typed workflow event written alongside).</summary>
    public DateTime? DecisionAtUtc { get; private set; }

    /// <summary>Required on rejection; optional on approval/cancellation.</summary>
    public string? DecisionComment { get; private set; }

    /// <summary>True while this row is the ticket's current cycle of its type; flipped by a superseding re-request, never deleted.</summary>
    public bool IsCurrent { get; private set; }

    /// <summary>Shared with the workflow event, status history, and audit rows written in the same operation.</summary>
    public Guid CorrelationId { get; private set; }

    private TicketApproval() { }

    private TicketApproval(
        long ticketId, ApprovalType approvalType,
        ApprovalTargetKind targetKind, int? targetDepartmentId, string? targetRoleName, Guid? targetEmployeeId,
        Guid requestedByEmployeeId, DateTime requestedAtUtc, string? requestComment, Guid correlationId)
    {
        TicketId = ticketId;
        ApprovalType = approvalType;
        Status = ApprovalStatus.Pending;
        TargetKind = targetKind;
        TargetDepartmentId = targetDepartmentId;
        TargetRoleName = targetRoleName;
        TargetEmployeeId = targetEmployeeId;
        RequestedByEmployeeId = requestedByEmployeeId;
        RequestedAtUtc = requestedAtUtc;
        RequestComment = requestComment;
        IsCurrent = true;
        CorrelationId = correlationId;
    }

    /// <summary>Opens a new Pending cycle from a configured requirement's target. Preventing a duplicate active cycle of the same type is the calling service's pre-check plus the filtered unique index.</summary>
    public static TicketApproval Request(
        long ticketId,
        RequestTypeApprovalRequirement requirement,
        Guid requestedByEmployeeId,
        DateTime requestedAtUtc,
        string? requestComment,
        Guid correlationId)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        if (requestedByEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("RequestedByEmployeeId is required.", nameof(requestedByEmployeeId));
        }

        return new TicketApproval(
            ticketId, requirement.ApprovalType,
            requirement.TargetKind, requirement.TargetDepartmentId, requirement.TargetRoleName, requirement.TargetEmployeeId,
            requestedByEmployeeId, requestedAtUtc, requestComment, correlationId);
    }

    public void Approve(Guid decidedByEmployeeId, DateTime decisionAtUtc, string? comment) =>
        Decide(ApprovalStatus.Approved, decidedByEmployeeId, decisionAtUtc, comment);

    /// <summary>A rejection always carries its why — the reason is mandatory, and what happens operationally afterward stays an explicit, separate action (never an automatic resolve/close).</summary>
    public void Reject(Guid decidedByEmployeeId, DateTime decisionAtUtc, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A rejection requires a reason.", nameof(reason));
        }

        Decide(ApprovalStatus.Rejected, decidedByEmployeeId, decisionAtUtc, reason);
    }

    /// <summary>Withdraws a still-pending request (e.g. raised in error). The record stays as history like every other outcome.</summary>
    public void Cancel(Guid decidedByEmployeeId, DateTime decisionAtUtc, string? comment) =>
        Decide(ApprovalStatus.Cancelled, decidedByEmployeeId, decisionAtUtc, comment);

    /// <summary>Flipped when a later cycle of the same type supersedes this one — append-only, this row is never deleted.</summary>
    public void MarkSuperseded() => IsCurrent = false;

    private void Decide(ApprovalStatus newStatus, Guid decidedByEmployeeId, DateTime decisionAtUtc, string? comment)
    {
        if (Status != ApprovalStatus.Pending)
        {
            throw new ApprovalAlreadyDecidedException(TicketApprovalId, Status);
        }

        if (decidedByEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("DecidedByEmployeeId is required.", nameof(decidedByEmployeeId));
        }

        Status = newStatus;
        DecidedByEmployeeId = decidedByEmployeeId;
        DecisionAtUtc = decisionAtUtc;
        DecisionComment = comment;
    }
}

/// <summary>Approval decisions are write-once — a second decision on the same cycle is a defect, not a state change.</summary>
public sealed class ApprovalAlreadyDecidedException(long ticketApprovalId, ApprovalStatus status)
    : TicketException($"Approval {ticketApprovalId} is already {status} — decisions are write-once; open a new cycle instead.")
{
    public long TicketApprovalId { get; } = ticketApprovalId;
    public ApprovalStatus Status { get; } = status;
}

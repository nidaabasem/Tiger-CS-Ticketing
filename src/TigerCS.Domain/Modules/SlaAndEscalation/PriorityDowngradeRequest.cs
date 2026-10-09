namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>MVP-ERD.md section 2.27 lifecycle of a <see cref="PriorityDowngradeRequest"/>.</summary>
public enum PriorityDowngradeRequestStatus : byte
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,

    /// <summary>A Pending request past <see cref="PriorityDowngradeRequest.ExpiresAtUtc"/>; it can no longer be decided.</summary>
    Expired = 4
}

/// <summary>
/// MVP-ERD.md section 2.27 / MVP-API-Contracts.md section 5.6 (Finding
/// DR-05) - separates "an agent asks for a priority decrease" from "a
/// Department Head approves it" into two actions by two independently
/// authenticated actors. <b>A downgrade never takes effect on request</b>
/// (ISSUE-023 Option B): creating this row changes neither the ticket's
/// priority nor its SLA period.
///
/// <para>
/// <b>Decider identity.</b> <see cref="DecidedByEmployeeId"/> is set only by
/// <see cref="Approve"/>/<see cref="Reject"/> from the authenticated caller;
/// no request field carries it. <see cref="RequestedByEmployeeId"/> may never
/// equal the decider (separation of duties; the contract is silent, so
/// self-approval is forbidden and this is documented as a decision).
/// </para>
/// </summary>
public class PriorityDowngradeRequest
{
    public long PriorityDowngradeRequestId { get; private set; }
    public long TicketId { get; private set; }

    /// <summary>The ticket's priority when the request was made. Approval is refused (stale) if the ticket's priority differs by then.</summary>
    public byte CurrentPriorityId { get; private set; }

    public byte RequestedPriorityId { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public PriorityDowngradeRequestStatus Status { get; private set; }

    public Guid RequestedByEmployeeId { get; private set; }
    public DateTime RequestedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }

    public Guid? DecidedByEmployeeId { get; private set; }
    public DateTime? DecidedAtUtc { get; private set; }
    public string? DecisionNote { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    private PriorityDowngradeRequest() { }

    private PriorityDowngradeRequest(
        long ticketId, byte currentPriorityId, byte requestedPriorityId, string reason,
        Guid requestedByEmployeeId, DateTime requestedAtUtc, DateTime expiresAtUtc)
    {
        TicketId = ticketId;
        CurrentPriorityId = currentPriorityId;
        RequestedPriorityId = requestedPriorityId;
        Reason = reason;
        RequestedByEmployeeId = requestedByEmployeeId;
        RequestedAtUtc = requestedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        Status = PriorityDowngradeRequestStatus.Pending;
    }

    /// <summary>Creates a Pending request. The reason is required; the new priority must be a genuine decrease (a numerically larger id).</summary>
    public static PriorityDowngradeRequest Create(
        long ticketId, byte currentPriorityId, byte requestedPriorityId, string reason,
        Guid requestedByEmployeeId, DateTime requestedAtUtc, DateTime expiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required.", nameof(reason));
        }

        if (requestedPriorityId <= currentPriorityId)
        {
            throw new ArgumentException("The requested priority is not a downgrade.", nameof(requestedPriorityId));
        }

        if (requestedByEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("The requester is required.", nameof(requestedByEmployeeId));
        }

        if (expiresAtUtc <= requestedAtUtc)
        {
            throw new ArgumentException("ExpiresAtUtc must follow RequestedAtUtc.", nameof(expiresAtUtc));
        }

        return new PriorityDowngradeRequest(
            ticketId, currentPriorityId, requestedPriorityId, reason.Trim(), requestedByEmployeeId, requestedAtUtc, expiresAtUtc);
    }

    /// <summary>True when still Pending but past its expiry - the state a read reports as Expired.</summary>
    public bool IsExpiredAt(DateTime nowUtc) => Status == PriorityDowngradeRequestStatus.Pending && nowUtc >= ExpiresAtUtc;

    /// <summary>The status a reader should see: a Pending request past expiry is reported Expired even before the sweep persists it.</summary>
    public PriorityDowngradeRequestStatus EffectiveStatusAt(DateTime nowUtc) =>
        IsExpiredAt(nowUtc) ? PriorityDowngradeRequestStatus.Expired : Status;

    public void MarkExpired(DateTime nowUtc)
    {
        if (!IsExpiredAt(nowUtc))
        {
            throw new InvalidOperationException("Only a Pending request past its expiry can be marked Expired.");
        }

        Status = PriorityDowngradeRequestStatus.Expired;
    }

    public void Approve(Guid deciderEmployeeId, DateTime nowUtc)
    {
        EnsureDecidable(deciderEmployeeId, nowUtc);
        Status = PriorityDowngradeRequestStatus.Approved;
        Decide(deciderEmployeeId, nowUtc, null);
    }

    public void Reject(Guid deciderEmployeeId, DateTime nowUtc, string decisionNote)
    {
        if (string.IsNullOrWhiteSpace(decisionNote))
        {
            throw new ArgumentException("A decision note is required to reject.", nameof(decisionNote));
        }

        EnsureDecidable(deciderEmployeeId, nowUtc);
        Status = PriorityDowngradeRequestStatus.Rejected;
        Decide(deciderEmployeeId, nowUtc, decisionNote.Trim());
    }

    private void Decide(Guid deciderEmployeeId, DateTime nowUtc, string? note)
    {
        DecidedByEmployeeId = deciderEmployeeId;
        DecidedAtUtc = nowUtc;
        DecisionNote = note;
    }

    private void EnsureDecidable(Guid deciderEmployeeId, DateTime nowUtc)
    {
        if (deciderEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("The deciding employee is required.", nameof(deciderEmployeeId));
        }

        if (Status != PriorityDowngradeRequestStatus.Pending)
        {
            throw new PriorityDowngradeRequestNotPendingException(PriorityDowngradeRequestId, Status);
        }

        if (IsExpiredAt(nowUtc))
        {
            throw new PriorityDowngradeRequestExpiredException(PriorityDowngradeRequestId);
        }

        if (deciderEmployeeId == RequestedByEmployeeId)
        {
            throw new PriorityDowngradeSelfDecisionException(PriorityDowngradeRequestId);
        }
    }
}

public sealed class PriorityDowngradeRequestNotPendingException(long requestId, PriorityDowngradeRequestStatus status)
    : Exception($"PriorityDowngradeRequest {requestId} is {status}, not Pending.");

public sealed class PriorityDowngradeRequestExpiredException(long requestId)
    : Exception($"PriorityDowngradeRequest {requestId} has expired.");

public sealed class PriorityDowngradeSelfDecisionException(long requestId)
    : Exception($"PriorityDowngradeRequest {requestId} cannot be decided by its own requester.");

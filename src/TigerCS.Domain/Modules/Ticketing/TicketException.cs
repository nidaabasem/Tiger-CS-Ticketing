namespace TigerCS.Domain.Modules.Ticketing;

public abstract class TicketException(string message) : Exception(message);

/// <summary>ISSUE-006 (approved): only Critical/High may proceed as a provisional ticket during a CRM outage — Medium/Low must remain queued in their IntakeRecord instead (see IntakeRecord.MarkPendingCrmVerification).</summary>
public sealed class ProvisionalTicketRequiresCriticalOrHighException(byte priorityId)
    : TicketException($"PriorityId {priorityId} is not Critical/High — provisional ticket creation is not permitted; the request must remain pending CRM verification.")
{
    public byte PriorityId { get; } = priorityId;
}

public sealed class TicketNotPendingCrmVerificationException(long ticketId, CrmVerificationStatus actualStatus)
    : TicketException($"Ticket {ticketId} cannot be reconciled — VerificationStatus is {actualStatus}, not PendingCrmVerification.")
{
    public long TicketId { get; } = ticketId;
    public CrmVerificationStatus ActualStatus { get; } = actualStatus;
}

/// <summary>Solution-Analysis.md §5.6 transition table / ADR-0008: the requested TicketStatus is not reachable from the ticket's current TicketStatus.</summary>
public sealed class InvalidTicketStatusTransitionException(long ticketId, TicketStatus fromStatus, TicketStatus toStatus)
    : TicketException($"Ticket {ticketId} cannot transition TicketStatus from {fromStatus} to {toStatus}.")
{
    public long TicketId { get; } = ticketId;
    public TicketStatus FromStatus { get; } = fromStatus;
    public TicketStatus ToStatus { get; } = toStatus;
}

/// <summary>MVP-API-Contracts.md §3.7's "must go through Open→InProgress with an assigned owner" requirement — a ticket cannot start being worked before it has an owner.</summary>
public sealed class TicketNotAssignedException(long ticketId)
    : TicketException($"Ticket {ticketId} has no current owner — assign it before starting work.");

/// <summary>Solution-Analysis.md §5.6: Resolve is only valid from InProgress/PendingCustomer/PendingThirdParty, never directly from Open or a terminal status.</summary>
public sealed class TicketNotEligibleForResolutionException(long ticketId, TicketStatus actualStatus)
    : TicketException($"Ticket {ticketId} cannot be resolved from TicketStatus {actualStatus}.")
{
    public long TicketId { get; } = ticketId;
    public TicketStatus ActualStatus { get; } = actualStatus;
}

/// <summary>MVP-API-Contracts.md §3.10: `409 not-yet-resolved` — Close requires a current TicketResolutions row.</summary>
public sealed class TicketNotYetResolvedException(long ticketId)
    : TicketException($"Ticket {ticketId} has no current resolution — resolve it before closing.")
{
    public long TicketId { get; } = ticketId;
}

/// <summary>MVP-ERD.md §2.10: DuplicateOfTicketId must reference a genuine, non-duplicate original — no chains of duplicates pointing to duplicates.</summary>
public sealed class DuplicateChainNotAllowedException(long ticketId, long duplicateOfTicketId)
    : TicketException($"Ticket {ticketId} cannot be marked a duplicate of {duplicateOfTicketId}, which is itself a duplicate.")
{
    public long TicketId { get; } = ticketId;
    public long DuplicateOfTicketId { get; } = duplicateOfTicketId;
}

/// <summary>MVP-ERD.md §2.3's write-once OriginatingDepartmentId / §3.6: a transfer to the ticket's own current department is not a valid transfer.</summary>
public sealed class TicketAlreadyInTargetDepartmentException(long ticketId, int departmentId)
    : TicketException($"Ticket {ticketId} is already in department {departmentId}.")
{
    public long TicketId { get; } = ticketId;
    public int DepartmentId { get; } = departmentId;
}

/// <summary>
/// Closed-ticket immutability (PR correction): a Closed ticket accepts no
/// further mutation of any kind — Assign, Transfer, ChangeStatus, Resolve,
/// and Close-again all reject with this exception before any state changes
/// or writes occur. Reading a Closed ticket's detail, history, and notes
/// remains unaffected — this exception is only ever thrown from a mutating
/// domain method.
/// </summary>
public sealed class TicketClosedException(long ticketId)
    : TicketException($"Ticket {ticketId} is Closed and no longer accepts mutations.")
{
    public long TicketId { get; } = ticketId;
}

/// <summary>
/// <c>Ticket.AcknowledgementSentAtUtc</c> is write-once (MVP-Data-Dictionary.md
/// §2.10). Thrown when a redelivered <c>TicketCreated</c> Outbox message
/// reaches a ticket whose automated acknowledgement was already delivered —
/// the domain-level half of "duplicate processing does not send twice"
/// (ADR-0013/ADR-0014). Callers treat it as "already done", not as an error.
/// </summary>
public sealed class AcknowledgementAlreadySentException(long ticketId, DateTime sentAtUtc)
    : TicketException($"Ticket {ticketId} already recorded its automated acknowledgement as sent at {sentAtUtc:O} — the field is write-once.")
{
    public long TicketId { get; } = ticketId;
    public DateTime SentAtUtc { get; } = sentAtUtc;
}

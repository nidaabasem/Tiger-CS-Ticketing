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

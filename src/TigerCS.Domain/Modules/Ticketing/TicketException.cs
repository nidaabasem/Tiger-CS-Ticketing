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

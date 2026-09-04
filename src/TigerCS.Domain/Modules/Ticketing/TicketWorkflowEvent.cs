namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// The typed business events a ticket's workflow can emit (Workflow/
/// Automation phase 3). These are the machine-queryable trigger source the
/// phase-4 SLA calculation reads — never parsed out of human audit text.
/// Values map onto the phase-1 <c>SlaTriggerType</c> concepts where a
/// trigger exists; the maintenance trio additionally carries the Handover
/// dependency state.
/// </summary>
public enum WorkflowEventType : byte
{
    /// <summary>An approval cycle was opened.</summary>
    ApprovalRequested = 1,

    /// <summary>A required approval was granted — the semantic event behind the <c>SlaTriggerType.ApprovalReceived</c> trigger (Collections / Send Receipts: the 1 day runs from here, computed in phase 4).</summary>
    ApprovalReceived = 2,

    /// <summary>A required approval was rejected. What happens next stays an explicit operational action.</summary>
    ApprovalRejected = 3,

    /// <summary>Customer Service approved — the semantic event behind <c>SlaTriggerType.CustomerServiceApproved</c> (Handover: 1–4 days run from here, computed in phase 4).</summary>
    CustomerServiceApproved = 4,

    /// <summary>Prerequisites are satisfied — the semantic event behind <c>SlaTriggerType.PrerequisitesCompleted</c> (Registration / Register Unit). Recorded explicitly by an authorized actor, never inferred.</summary>
    PrerequisitesCompleted = 5,

    /// <summary>Handover: maintenance exists and the process waits on it. No duration is attached — the source defines none.</summary>
    MaintenanceRequired = 6,

    /// <summary>Handover: no maintenance dependency — after the NOC the customer proceeds to the tower/key handover.</summary>
    MaintenanceNotRequired = 7,

    /// <summary>Handover: the maintenance dependency completed. Not customer-caused; how it affects the clock is a phase-4 decision.</summary>
    MaintenanceCompleted = 8
}

/// <summary>
/// One typed, append-only workflow event of a ticket — the trustworthy
/// timestamp store for conditional SLA triggers and dependency state.
/// Written in the same transaction (and under the same correlation id) as
/// the approval/audit rows describing the same action; never updated, never
/// deleted. Distinct from <see cref="TicketStatusHistory"/> (which records
/// the five lifecycle dimensions) and from <c>AuditEntries</c> (the
/// human-auditable trail): this table exists precisely so phase 4 reads
/// typed events instead of parsing either of those.
/// </summary>
public class TicketWorkflowEvent
{
    public long TicketWorkflowEventId { get; private set; }
    public long TicketId { get; private set; }
    public WorkflowEventType EventType { get; private set; }

    /// <summary>When the business event happened — the value a phase-4 SLA trigger reads.</summary>
    public DateTime OccurredAtUtc { get; private set; }

    /// <summary>Null = the system. Approval-linked events carry the deciding/requesting employee.</summary>
    public Guid? ActorEmployeeId { get; private set; }

    /// <summary>The approval cycle this event belongs to, for the four approval event types; null for prerequisite/maintenance events.</summary>
    public long? TicketApprovalId { get; private set; }

    /// <summary>Short human context (e.g. the recorded reason) — display only; consumers key on <see cref="EventType"/> and <see cref="OccurredAtUtc"/>, never on this text.</summary>
    public string? Note { get; private set; }

    public Guid CorrelationId { get; private set; }

    private TicketWorkflowEvent() { }

    public TicketWorkflowEvent(
        long ticketId,
        WorkflowEventType eventType,
        DateTime occurredAtUtc,
        Guid? actorEmployeeId,
        long? ticketApprovalId,
        string? note,
        Guid correlationId)
    {
        if (!Enum.IsDefined(eventType))
        {
            throw new ArgumentException($"EventType {eventType} is not a defined workflow event type.", nameof(eventType));
        }

        TicketId = ticketId;
        EventType = eventType;
        OccurredAtUtc = occurredAtUtc;
        ActorEmployeeId = actorEmployeeId;
        TicketApprovalId = ticketApprovalId;
        Note = note;
        CorrelationId = correlationId;
    }
}

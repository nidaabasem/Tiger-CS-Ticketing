namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// The conceptual step kinds a <see cref="WorkflowTemplateStep"/> can carry.
///
/// <para>
/// <b>Deliberately not a second <c>TicketStatus</c>.</b> The values that
/// correspond to lifecycle states (<see cref="Created"/>, <see cref="InProgress"/>,
/// <see cref="PendingCustomer"/>, <see cref="PendingInternal"/>,
/// <see cref="Resolved"/>, <see cref="Closed"/>) describe which existing
/// <c>TicketStatus</c>/lifecycle event a step maps onto; the three that do
/// not (<see cref="Assigned"/> — the assignment dimension;
/// <see cref="Review"/>/<see cref="WaitingForApproval"/> — the approval
/// concept) are workflow-step concepts realized as assignment/approval
/// records over the unchanged status machine, per the phase decision to
/// prefer approval records over new <c>TicketStatus</c> values. Nothing in
/// this enum is written to <c>Tickets.TicketStatus</c>.
/// </para>
/// </summary>
public enum WorkflowStepKind : byte
{
    /// <summary>Ticket created — maps to <c>TicketStatus.Open</c>.</summary>
    Created = 1,

    /// <summary>Assignment to a department/employee — the assignment dimension, not a status value.</summary>
    Assigned = 2,

    /// <summary>The responsible team reviews the request before work/approval — an approval-flow concept (Phase 3), not a status value.</summary>
    Review = 3,

    /// <summary>Waiting on a configured approver (e.g. Accounting for Send Receipts, Customer Service for Handover) — an approval-record concept (Phase 3), not a status value.</summary>
    WaitingForApproval = 4,

    /// <summary>Active work — maps to <c>TicketStatus.InProgress</c>.</summary>
    InProgress = 5,

    /// <summary>Waiting on the customer (payment, documents, response) — maps to <c>TicketStatus.PendingCustomer</c>.</summary>
    PendingCustomer = 6,

    /// <summary>Waiting on another internal department or an external party — maps to <c>TicketStatus.PendingThirdParty</c>.</summary>
    PendingInternal = 7,

    /// <summary>The responsible team considers its work completed — maps to <c>TicketStatus.Resolved</c>.</summary>
    Resolved = 8,

    /// <summary>Customer Service considers the case completed — maps to <c>TicketStatus.Closed</c>.</summary>
    Closed = 9
}

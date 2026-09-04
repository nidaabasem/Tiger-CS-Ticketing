using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>Approval cycles (Workflow/Automation phase 3) — append-plus-supersede, never deleted.</summary>
public interface ITicketApprovalRepository
{
    Task<TicketApproval?> GetByIdAsync(long ticketApprovalId, CancellationToken cancellationToken = default);

    /// <summary>The still-Pending cycle of one type, or null. At most one exists (filtered unique index).</summary>
    Task<TicketApproval?> GetPendingAsync(long ticketId, ApprovalType approvalType, CancellationToken cancellationToken = default);

    /// <summary>The current (latest) cycle of one type, whatever its status, or null when the type was never requested.</summary>
    Task<TicketApproval?> GetCurrentAsync(long ticketId, ApprovalType approvalType, CancellationToken cancellationToken = default);

    /// <summary>Every cycle of the ticket, oldest first — full history, superseded rows included.</summary>
    Task<IReadOnlyList<TicketApproval>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default);

    Task AddAsync(TicketApproval approval, CancellationToken cancellationToken = default);
}

/// <summary>The typed workflow event store phase 4's conditional SLA triggers read — append-only.</summary>
public interface ITicketWorkflowEventRepository
{
    /// <summary>The FIRST event of one type for a ticket (the trigger-timestamp read), or null when it never occurred.</summary>
    Task<TicketWorkflowEvent?> GetFirstAsync(long ticketId, WorkflowEventType eventType, CancellationToken cancellationToken = default);

    /// <summary>The LATEST event among the given types — how the UI derives dependency state (e.g. maintenance Required vs NotRequired vs Completed).</summary>
    Task<TicketWorkflowEvent?> GetLatestAsync(long ticketId, IReadOnlyCollection<WorkflowEventType> eventTypes, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TicketWorkflowEvent>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default);

    Task AddAsync(TicketWorkflowEvent workflowEvent, CancellationToken cancellationToken = default);
}

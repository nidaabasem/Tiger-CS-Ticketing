using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>What the agent work list is filtered by. Every field is optional; an empty query is "all outstanding work I can see".</summary>
/// <param name="VisibleDepartmentIds">Department scoping, resolved exactly as the ticket queue resolves it. Null means every department (a CS-layer role).</param>
/// <param name="DepartmentId">Narrow to one department.</param>
/// <param name="ChannelId">Narrow to one channel — the reason this list is not a "callback list".</param>
/// <param name="AssignedEmployeeId">Narrow to one agent's own work.</param>
/// <param name="UnassignedOnly">Only work nobody has taken yet.</param>
/// <param name="IncludeResolved">Include completed/cancelled work; by default the list shows only what is still outstanding.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Rows per page.</param>
public sealed record AgentHandoffQuery(
    IReadOnlyCollection<int>? VisibleDepartmentIds = null,
    int? DepartmentId = null,
    byte? ChannelId = null,
    Guid? AssignedEmployeeId = null,
    bool UnassignedOnly = false,
    bool IncludeResolved = false,
    int Page = 1,
    int PageSize = 50);

/// <summary>One row of the work list, with the context an agent needs to decide without opening the ticket first.</summary>
public sealed record AgentHandoffListRow(
    TicketAgentHandoff Handoff,
    string TicketNumber,
    string RequestSummary,
    string TicketStatus,
    bool TicketIsClassified,
    string? CustomerName,
    string? CustomerPhone,
    string? GenesysConversationId,
    bool InteractionEnded);

public sealed record AgentHandoffQueryResult(IReadOnlyList<AgentHandoffListRow> Items, int TotalCount);

/// <summary>
/// Pending human work — the channel-neutral "someone needs an agent" record.
/// Separate from <c>ITicketInteractionRepository</c> because the work has its
/// own lifecycle that outlives the interaction it came from.
/// </summary>
public interface ITicketAgentHandoffRepository
{
    Task<TicketAgentHandoff?> GetByIdAsync(long ticketAgentHandoffId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The still-outstanding handoff for one interaction, or null. This is the
    /// application-level half of the idempotency guarantee; the filtered
    /// unique index is the half that survives a concurrent redelivery.
    /// </summary>
    Task<TicketAgentHandoff?> GetOpenByInteractionIdAsync(long ticketInteractionId, CancellationToken cancellationToken = default);

    /// <summary>The handoff carrying a Genesys work-item id, or null — the stronger idempotency key, used only when Genesys supplies one.</summary>
    Task<TicketAgentHandoff?> GetByExternalWorkItemIdAsync(string externalWorkItemId, CancellationToken cancellationToken = default);

    /// <summary>A ticket's handoffs, newest request first — the Ticket Details history.</summary>
    Task<IReadOnlyList<TicketAgentHandoff>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default);

    /// <summary>The agent work list.</summary>
    Task<AgentHandoffQueryResult> SearchAsync(AgentHandoffQuery query, CancellationToken cancellationToken = default);

    Task AddAsync(TicketAgentHandoff handoff, CancellationToken cancellationToken = default);
}

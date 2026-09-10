namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>
/// One piece of pending human work as the agent work list and Ticket Details
/// see it.
/// </summary>
/// <param name="TicketAgentHandoffId">The work item.</param>
/// <param name="TicketId">The ticket the work belongs to — never a new one.</param>
/// <param name="TicketNumber">The ticket's human-facing number. Empty on a mutation response, which reports the work item rather than re-reading the ticket.</param>
/// <param name="TicketInteractionId">The interaction the work arose from.</param>
/// <param name="GenesysConversationId">The Genesys conversation behind that interaction, when it had one.</param>
/// <param name="DepartmentId">The department the work belongs to.</param>
/// <param name="ChannelId">The channel the customer used.</param>
/// <param name="Status">One of NotRequired, WaitingForAgent, Assigned, InProgress, Completed, Cancelled — TigerCS' own vocabulary, not Genesys'.</param>
/// <param name="Mode">Callback, ContinueChat, ReplyInChannel or HumanTakeover — or null when the caller did not say. Never inferred from the channel.</param>
/// <param name="RequestReason">Why a human was needed, as reported (e.g. a virtual agent's escalation reason). Null when not supplied.</param>
/// <param name="RequestedAtUtc">When a human was first asked for — what "waiting since" is measured from.</param>
/// <param name="AssignedEmployeeId">The TigerCS employee handling it, when known.</param>
/// <param name="GenesysAgentId">Genesys' own agent identifier, when supplied — recorded even when no TigerCS employee matches.</param>
/// <param name="AssignedAtUtc">When an agent was named.</param>
/// <param name="StartedAtUtc">When an agent began handling it.</param>
/// <param name="CompletedAtUtc">When the work was completed.</param>
/// <param name="ResolvedAtUtc">When the work stopped being outstanding, by completion or cancellation. Null exactly while it is still pending.</param>
/// <param name="ResolutionNote">What the agent recorded on finishing, or the required reason for cancelling.</param>
/// <param name="CustomerName">The customer's name as the channel collected it.</param>
/// <param name="CustomerPhone">The customer's number for this interaction.</param>
/// <param name="RequestSummary">The ticket's one-line summary. Empty on a mutation response.</param>
/// <param name="TicketStatus">The ticket's own status — separate from this work item's, and never changed by it. Empty on a mutation response.</param>
/// <param name="TicketIsClassified">False while the ticket is still Unclassified — pending human work never waits for classification.</param>
/// <param name="InteractionEnded">Whether the live conversation already ended. The work stays actionable either way: a customer who closed the browser still needs an answer.</param>
/// <param name="ExternalWorkItemId">Genesys' own routing-task/work-item id, when it supplies one. External identifier only.</param>
public sealed record AgentHandoffDto(
    long TicketAgentHandoffId,
    long TicketId,
    string TicketNumber,
    long TicketInteractionId,
    string? GenesysConversationId,
    int DepartmentId,
    byte ChannelId,
    string Status,
    string? Mode,
    string? RequestReason,
    DateTime RequestedAtUtc,
    Guid? AssignedEmployeeId,
    string? GenesysAgentId,
    DateTime? AssignedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    DateTime? ResolvedAtUtc,
    string? ResolutionNote,
    string? CustomerName,
    string? CustomerPhone,
    string RequestSummary,
    string TicketStatus,
    bool TicketIsClassified,
    bool InteractionEnded,
    string? ExternalWorkItemId);

public sealed record AgentHandoffListResultDto(
    IReadOnlyList<AgentHandoffDto> Items, int TotalCount, int Page, int PageSize);

/// <summary>Filters for the agent work list. All optional.</summary>
public sealed record AgentHandoffListRequestDto(
    int? DepartmentId = null,
    byte? ChannelId = null,
    Guid? AssignedEmployeeId = null,
    bool UnassignedOnly = false,
    bool IncludeResolved = false,
    int Page = 1,
    int PageSize = 50);

/// <summary>Finishing a piece of human work. The note is optional here and required when cancelling.</summary>
public sealed record CompleteAgentHandoffRequestDto(string? ResolutionNote = null);

/// <summary>Cancelling pending human work — a reason is required, because work is never dropped without a recorded why.</summary>
public sealed record CancelAgentHandoffRequestDto(string Reason);

/// <summary>What an agent-facing handoff operation did.</summary>
public enum AgentHandoffOutcome
{
    Success,

    NotFound,

    /// <summary>The caller may not act on work in this department.</summary>
    Forbidden,

    /// <summary>The work was already Completed or Cancelled — a redelivered or double-clicked action, treated as already done rather than as a failure.</summary>
    AlreadyResolved,

    /// <summary>Cancellation was attempted without a reason.</summary>
    ReasonRequired
}

public sealed record AgentHandoffResult(AgentHandoffOutcome Outcome, AgentHandoffDto? Handoff = null)
{
    public static AgentHandoffResult Success(AgentHandoffDto handoff) => new(AgentHandoffOutcome.Success, handoff);
    public static AgentHandoffResult Failure(AgentHandoffOutcome outcome) => new(outcome);
}

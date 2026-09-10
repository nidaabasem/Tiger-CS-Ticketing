namespace TigerCS.Application.Modules.GenesysIntegration.Dto;

/// <summary>
/// Everything Genesys may update on a ticket it created — and, by omission,
/// everything it may not.
///
/// <para>
/// <b>This is deliberately a narrow surface.</b> Genesys owns the
/// conversation: who handled it, when it ended, what was said, and whether it
/// still needs a human. It does <b>not</b> own the ticket's business state.
/// There is no field here for category, request type, priority, status,
/// owner, department, resolution or closure — those move only through their
/// own TigerCS operations, with their own rules, authorization and SLA
/// consequences. A field absent from this contract is a field Genesys cannot
/// reach.
/// </para>
///
/// <para>
/// Every part is optional and independently idempotent: send only what
/// changed, resend freely. In particular, ending a conversation that already
/// ended does not move the recorded end time or duplicate its transcript, and
/// <b>no update here ever closes the ticket</b>.
/// </para>
/// </summary>
/// <param name="ConversationId">
/// Required. The Genesys conversation this update belongs to. It must resolve
/// to an interaction on the ticket in the route — that cross-check is what
/// stops a transcript being applied to the wrong ticket.
/// </param>
/// <param name="AgentId">The Genesys agent now handling the conversation, when known. Applied only if the interaction does not already name one.</param>
/// <param name="AgentName">That agent's display name, same apply-if-absent rule.</param>
/// <param name="Ended">Set when the conversation has finished, for any reason — agent ended it, customer closed the browser, connection dropped, Genesys timed it out.</param>
/// <param name="Handoff">Set when the conversation needs a human agent, or when one has taken it. Omit entirely when neither applies.</param>
public sealed record GenesysTicketUpdateDto(
    string ConversationId,
    string? AgentId = null,
    string? AgentName = null,
    GenesysConversationEndUpdateDto? Ended = null,
    GenesysHandoffUpdateDto? Handoff = null);

/// <summary>
/// The conversation has finished. Recording this stores the transcript and
/// finalizes the interaction — and deliberately leaves the ticket exactly
/// where the TigerCS workflow has it.
/// </summary>
/// <param name="EndedAtUtc">When it ended. Defaults to now when omitted.</param>
/// <param name="EndReason">Why, as reported (e.g. "AgentDisconnect", "CustomerDisconnect", "Timeout"). Free text — no vocabulary is confirmed.</param>
/// <param name="Transcript">The conversation, in order, as far as it is available. Resending an already-stored transcript stores nothing twice.</param>
public sealed record GenesysConversationEndUpdateDto(
    DateTime? EndedAtUtc = null,
    string? EndReason = null,
    IReadOnlyList<GenesysTranscriptMessageDto>? Transcript = null);

/// <summary>
/// Human-agent state for the conversation, on any channel. A callback is one
/// possible <paramref name="Mode"/>, never the concept.
/// </summary>
/// <param name="Required">True to record that the conversation needs a human agent. Idempotent — a conversation whose human work is still outstanding is answered with that same work item.</param>
/// <param name="AgentAvailable">True when Genesys is handing straight over to a named human, false when nobody is available and the work must wait.</param>
/// <param name="Mode">"Callback", "ContinueChat", "ReplyInChannel" or "HumanTakeover". Omit unless Genesys actually states it — TigerCS never derives it from the channel.</param>
/// <param name="Reason">Why a human is needed (a virtual agent's escalation reason, a routing note).</param>
/// <param name="WorkItemId">Genesys' own routing-task id, if it has one. The stronger idempotency key; never invented in its absence.</param>
/// <param name="AssignedAgentId">Set to record that this Genesys agent has taken the outstanding work. Applies to the existing work item, never a second one.</param>
public sealed record GenesysHandoffUpdateDto(
    bool Required = false,
    bool AgentAvailable = false,
    string? Mode = null,
    string? Reason = null,
    string? WorkItemId = null,
    string? AssignedAgentId = null);

/// <summary>What a Genesys ticket update did.</summary>
public enum GenesysTicketUpdateOutcome
{
    /// <summary>Everything supplied was applied (or was already in that state).</summary>
    Applied,

    IntegrationDisabled,
    ConversationIdRequired,

    /// <summary>No interaction exists for this conversation — it never produced a ticket.</summary>
    ConversationNotFound,

    /// <summary>No such ticket.</summary>
    TicketNotFound,

    /// <summary>The conversation belongs to a different ticket than the one in the route. Refused rather than guessing which the caller meant.</summary>
    ConversationTicketMismatch,

    /// <summary>A transcript message had an unrecognized sender or an empty body. NOTHING was stored — resend the whole update.</summary>
    InvalidTranscript,

    /// <summary>The supplied handoff mode is not one of TigerCS' normalized values.</summary>
    InvalidHandoffMode,

    /// <summary>An assignment was supplied but there is no outstanding human work to apply it to.</summary>
    NoOpenHandoff
}

/// <summary>
/// The result of one update. <see cref="TicketStatus"/> is echoed
/// deliberately: it is the proof that ending a conversation did not close the
/// ticket.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="TicketId">The ticket.</param>
/// <param name="TicketNumber">Its number.</param>
/// <param name="TicketStatus">Its status — unchanged by anything in this contract.</param>
/// <param name="ConversationEnded">Whether the interaction is now recorded as ended.</param>
/// <param name="TranscriptMessageCount">How many transcript messages the interaction holds after this update.</param>
/// <param name="HandoffStatus">The pending human work's status, when there is any: WaitingForAgent, Assigned, InProgress, Completed or Cancelled. Null when this conversation never needed a human.</param>
/// <param name="TicketAgentHandoffId">That work item's id, when there is one.</param>
/// <param name="Detail">A human-readable explanation on a refusal.</param>
public sealed record GenesysTicketUpdateResult(
    GenesysTicketUpdateOutcome Outcome,
    long? TicketId = null,
    string? TicketNumber = null,
    string? TicketStatus = null,
    bool ConversationEnded = false,
    int TranscriptMessageCount = 0,
    string? HandoffStatus = null,
    long? TicketAgentHandoffId = null,
    string? Detail = null)
{
    public static GenesysTicketUpdateResult Failure(GenesysTicketUpdateOutcome outcome, string? detail = null) =>
        new(outcome, Detail: detail);
}

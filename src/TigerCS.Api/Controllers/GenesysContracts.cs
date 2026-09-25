using TigerCS.Application.Modules.GenesysIntegration.Dto;

namespace TigerCS.Api.Controllers;

/// <summary>
/// The <b>transport</b> shape of an inbound Genesys inquiry — the outermost
/// edge of the integration, and the only place a Genesys-facing wire format
/// is allowed to exist. Its whole job is to be mapped onto the normalized
/// <see cref="GenesysInquiryDto"/> the application layer understands, so no
/// domain or application code depends on how Genesys words anything.
///
/// <para>
/// <b>This is TigerCS' contract, not a reproduction of Genesys'.</b> No
/// official Genesys webhook schema, event-topic vocabulary, authentication
/// mechanism or payload has been confirmed to this repository, so none is
/// invented here: this endpoint asks for exactly the facts Ticketing needs
/// and nothing more. In particular there is <b>no event field</b> — posting
/// this body means "create or reuse a ticket for this conversation", and
/// nothing else. A ringing call never reaches TigerCS. When the Genesys team supplies their real contract, the
/// adapter that translates it lives at this boundary — this record changes
/// or gains a sibling, and nothing behind it moves.
/// </para>
/// </summary>
/// <param name="ConversationId">Required. Genesys' conversation id — the idempotency key: the same value always resolves to the same ticket.</param>
/// <param name="Channel">Required. One of "Phone", "LiveChat", "WhatsApp", "SocialMedia" (case-insensitive). "WebsiteChat" and "WebMessaging" (Genesys' own name for its web chat) are accepted as the same Live Chat channel.</param>
/// <param name="InteractionId">Genesys' interaction id, where it differs from the conversation id.</param>
/// <param name="ParticipantId">Genesys' customer-participant id, where available.</param>
/// <param name="CommunicationId">Genesys' communication id, where available.</param>
/// <param name="Direction">"Inbound"/"Outbound", where available.</param>
/// <param name="CustomerPhone">The customer's number — what the existing TigerCS customer lookup searches with. A telephony address is accepted as Genesys reports it (<c>Call.Ani</c>: "tel:+971…"); it is normalized to "+971…" before it is searched or stored.</param>
/// <param name="CustomerName">The customer's name, where the channel collected one (e.g. the website chat form).</param>
/// <param name="CustomerEmail">The customer's email, where the channel collected one.</param>
/// <param name="CalledNumber">The Tiger number the customer dialed. Recorded, never used for routing.</param>
/// <param name="QueueId">The Genesys queue — resolved to a department via the configured queue mapping when the customer chose no department.</param>
/// <param name="QueueName">The queue's display name, where available.</param>
/// <param name="AgentId">The handling Genesys agent's <b>Genesys User ID</b> (the immutable id, not a name), where available. When supplied, it is resolved server-side to the mapped Ticketing user and that user is recorded as the interaction's handler; an unmapped id is recorded verbatim with no handler.</param>
/// <param name="AgentName">The handling agent's display name, where available. Display only — never an identity key.</param>
/// <param name="StartedAtUtc">When the interaction started, UTC.</param>
/// <param name="DepartmentId">The department the customer explicitly selected (website chat). Wins over the queue mapping.</param>
/// <param name="DepartmentCode">The same explicit selection as a TigerCS department code, for a caller that knows codes rather than ids.</param>
/// <param name="TowerName">The tower/project the customer typed into the chat form, where collected.</param>
/// <param name="UnitNumber">The unit number the customer typed into the chat form, where collected.</param>
/// <param name="Subject">A short subject the channel collected, where available — becomes the ticket's request summary.</param>
public sealed record GenesysInquiryRequest(
    string ConversationId,
    string Channel,
    string? InteractionId = null,
    string? ParticipantId = null,
    string? CommunicationId = null,
    string? Direction = null,
    string? CustomerPhone = null,
    string? CustomerName = null,
    string? CustomerEmail = null,
    string? CalledNumber = null,
    string? QueueId = null,
    string? QueueName = null,
    string? AgentId = null,
    string? AgentName = null,
    DateTime? StartedAtUtc = null,
    int? DepartmentId = null,
    string? DepartmentCode = null,
    string? TowerName = null,
    string? UnitNumber = null,
    string? Subject = null);

/// <summary>The transport shape of a conversation-end report, with the transcript available up to that moment. See <see cref="GenesysInquiryRequest"/> for why this record exists at the edge.</summary>
/// <param name="ConversationId">Required. The conversation being ended — the id it was ingested under.</param>
/// <param name="EndedAtUtc">When it ended, UTC. The server's clock is used when absent.</param>
/// <param name="EndReason">Why it ended, as reported ("AgentDisconnect", "CustomerDisconnect", "Timeout", …).</param>
/// <param name="AgentId">The handling agent, where the end event names one.</param>
/// <param name="AgentName">The handling agent's display name.</param>
/// <param name="Transcript">The conversation's messages, in order. Absent/empty for a voice call.</param>
public sealed record GenesysConversationEndRequest(
    string ConversationId,
    DateTime? EndedAtUtc = null,
    string? EndReason = null,
    string? AgentId = null,
    string? AgentName = null,
    IReadOnlyList<GenesysTranscriptMessageRequest>? Transcript = null);

/// <summary>One transcript message, at the transport edge.</summary>
/// <param name="Sender">Required. "Customer", "Agent" or "System" (case-insensitive).</param>
/// <param name="SentAtUtc">Required. When the message was sent, UTC.</param>
/// <param name="Body">Required. The message text, verbatim.</param>
/// <param name="SenderName">The sender's display name, where available.</param>
/// <param name="SenderId">The sender's Genesys participant/agent id, where available.</param>
/// <param name="ExternalMessageId">Genesys' own message id, where available.</param>
public sealed record GenesysTranscriptMessageRequest(
    string Sender,
    DateTime SentAtUtc,
    string Body,
    string? SenderName = null,
    string? SenderId = null,
    string? ExternalMessageId = null);

/// <summary>What the ingestion endpoint answers — always naming the ticket, so a retry and the original call are indistinguishable to the caller.</summary>
/// <param name="Outcome">"TicketCreated" or "AlreadyIngested".</param>
/// <param name="ConversationId">The conversation this refers to, echoed back.</param>
/// <param name="TicketId">The one ticket this conversation produced.</param>
/// <param name="TicketNumber">That ticket's human-facing number.</param>
public sealed record GenesysInquiryAcceptedResponse(string Outcome, string ConversationId, long TicketId, string TicketNumber);

/// <summary>What the conversation-end endpoint answers. <paramref name="TicketStatus"/> is included deliberately: it shows that ending a conversation did NOT close the ticket.</summary>
/// <param name="Outcome">"Ended" or "AlreadyEnded".</param>
/// <param name="ConversationId">The conversation, echoed back.</param>
/// <param name="TicketId">The ticket the conversation belongs to.</param>
/// <param name="TicketNumber">That ticket's number.</param>
/// <param name="TicketStatus">The ticket's status — unchanged by the conversation ending.</param>
/// <param name="TranscriptMessageCount">How many transcript messages the interaction now holds.</param>
public sealed record GenesysConversationEndResponse(
    string Outcome,
    string ConversationId,
    long? TicketId,
    string? TicketNumber,
    string? TicketStatus,
    int TranscriptMessageCount);

/// <summary>
/// The one Genesys update body. Every part is optional and independently
/// idempotent — send only what changed, and resend freely.
///
/// <para>
/// <b>Deliberately narrow.</b> There is no field for category, request type,
/// priority, status, owner, department, resolution or closure: Genesys owns
/// the conversation, TigerCS owns the ticket's business state, and a field
/// absent from this contract is one Genesys cannot reach.
/// </para>
/// </summary>
/// <param name="ConversationId">Required. Must resolve to an interaction on the ticket in the route.</param>
/// <param name="AgentId">The Genesys User ID of the agent handling it, when known. Applied only if the interaction does not already name one; when it maps to a Ticketing user, that user is recorded as the interaction's handler (same apply-if-absent rule).</param>
/// <param name="AgentName">That agent's display name, same apply-if-absent rule. Display only — never an identity key.</param>
/// <param name="Ended">Set when the conversation has finished, for any reason. <b>Never closes the ticket.</b></param>
/// <param name="Handoff">Set when the conversation needs a human agent, or when one has taken it.</param>
/// <param name="StartedAtUtc">When the interaction started, UTC — recorded only if the Create call did not carry it. Never moves once known.</param>
/// <param name="Routing">Set when the conversation moved to another queue, or an agent connected / it was transferred. Same ticket, always.</param>
public sealed record GenesysTicketUpdateRequest(
    string ConversationId,
    string? AgentId = null,
    string? AgentName = null,
    GenesysConversationEndPart? Ended = null,
    GenesysHandoffPart? Handoff = null,
    DateTime? StartedAtUtc = null,
    GenesysRoutingPart? Routing = null);

/// <summary>
/// Where the conversation is now: its current queue and connected agent. Send
/// it when the conversation enters a queue, when an agent connects, and on
/// every transfer. It never creates a ticket and never moves the ticket's
/// department, owner or status; when an agent connects to human work that is
/// still waiting for one, that work is recorded as taken by them.
/// </summary>
/// <param name="QueueId">The Genesys queue id the conversation is now in.</param>
/// <param name="QueueName">That queue's name.</param>
/// <param name="AgentId">The Genesys User ID of the agent now connected.</param>
/// <param name="AgentName">That agent's display name. Display only.</param>
public sealed record GenesysRoutingPart(
    string? QueueId = null,
    string? QueueName = null,
    string? AgentId = null,
    string? AgentName = null);

/// <summary>The conversation has finished — for any reason: the agent ended it, the customer closed the browser, the connection dropped, Genesys timed it out.</summary>
/// <param name="EndedAtUtc">When it ended. Defaults to now.</param>
/// <param name="EndReason">Why, as reported (e.g. "AgentDisconnect", "CustomerDisconnect", "Timeout"). Free text — no vocabulary is confirmed.</param>
/// <param name="Transcript">The conversation in order, as far as available. Resending an already-stored transcript stores nothing twice.</param>
public sealed record GenesysConversationEndPart(
    DateTime? EndedAtUtc = null,
    string? EndReason = null,
    IReadOnlyList<GenesysTranscriptMessageRequest>? Transcript = null);

/// <summary>Human-agent state for the conversation, on any channel. A callback is one possible <paramref name="Mode"/>, never the concept.</summary>
/// <param name="Required">
/// <c>true</c> records that the conversation needs a human agent — idempotent,
/// as outstanding work is answered with that same work item.
/// <c>false</c> <b>stands outstanding work down</b> (the AI reconnected and
/// resumed, or the human is no longer needed), which requires
/// <paramref name="Reason"/> and leaves the ticket untouched; nothing
/// outstanding is not an error.
/// <b>Omit it</b> to do neither — an update reporting only
/// <paramref name="AssignedAgentId"/> must not read as a cancellation, which
/// is why this is nullable rather than defaulting to false.
/// </param>
/// <param name="Trigger">Why a human is needed, typed: "CustomerRequestedHuman", "AiConnectionLost", "AiEscalated", "RoutingDecision" or "AgentTransfer". <b>Omit it</b> unless Genesys states it — TigerCS never derives it from the channel or the end reason.</param>
/// <param name="AgentAvailable">True when Genesys is handing straight over to a named human; false when nobody is available and the work must wait.</param>
/// <param name="Mode">"Callback", "ContinueChat", "ReplyInChannel" or "HumanTakeover". <b>Omit it</b> unless Genesys actually states it — TigerCS never derives it from the channel.</param>
/// <param name="Reason">Why a human is needed (a virtual agent's escalation reason, a routing note).</param>
/// <param name="WorkItemId">Genesys' own routing-task id, if it has one — the stronger idempotency key. Omit if Genesys has none.</param>
/// <param name="AssignedAgentId">Set to record that this Genesys agent took the outstanding work. Applies to the existing work item, never a second one.</param>
public sealed record GenesysHandoffPart(
    bool? Required = null,
    bool AgentAvailable = false,
    string? Mode = null,
    string? Reason = null,
    string? Trigger = null,
    string? WorkItemId = null,
    string? AssignedAgentId = null);

/// <summary>What the update endpoint answers. <paramref name="TicketStatus"/> is echoed deliberately: it is the proof that ending a conversation did not close the ticket.</summary>
/// <param name="Outcome">"Applied".</param>
/// <param name="ConversationId">The conversation, echoed back.</param>
/// <param name="TicketId">The ticket.</param>
/// <param name="TicketNumber">Its number.</param>
/// <param name="TicketStatus">Its status — unchanged by anything in this contract.</param>
/// <param name="ConversationEnded">Whether the interaction is now recorded as ended.</param>
/// <param name="TranscriptMessageCount">How many transcript messages the interaction holds after this update.</param>
/// <param name="HandoffStatus">The pending human work's status when there is any: WaitingForAgent, Assigned, InProgress, Completed or Cancelled. Null when this conversation never needed a human.</param>
/// <param name="TicketAgentHandoffId">That work item's id, when there is one.</param>
public sealed record GenesysTicketUpdateResponse(
    string Outcome,
    string ConversationId,
    long TicketId,
    string TicketNumber,
    string TicketStatus,
    bool ConversationEnded,
    int TranscriptMessageCount,
    string? HandoffStatus,
    long? TicketAgentHandoffId);

/// <summary>
/// The transport shape of a Genesys <b>agent action</b>: the Ticketing screen
/// opened from Genesys identifying which agent is working, and on which
/// conversation. This is the strict agent endpoint — <c>genesysUserId</c> is
/// required and must be mapped to an active Ticketing user.
///
/// <para>
/// <b>There is deliberately no Ticketing user id in this body.</b> Which
/// Ticketing user handled the interaction is resolved on the server from
/// <paramref name="GenesysUserId"/> alone; any such property a client sends
/// is ignored by model binding, never honoured.
/// </para>
/// </summary>
/// <param name="GenesysUserId">Required. The agent's immutable Genesys User ID — the only value used to identify the agent.</param>
/// <param name="AgentEmail">The agent's email as Genesys knows it. Informational only: recorded on the audit trail, never used to resolve the agent.</param>
/// <param name="ConversationId">The conversation the agent is working, when there is one. Its interaction then records the resolved user as the handler.</param>
public sealed record GenesysAgentContextRequest(
    string? GenesysUserId,
    string? AgentEmail = null,
    string? ConversationId = null);

/// <summary>What the agent-context endpoint answers: who the agent is in Ticketing, and — when a conversation was named — which ticket and interaction it belongs to.</summary>
/// <param name="Outcome">"Resolved".</param>
/// <param name="GenesysUserId">The Genesys User ID, echoed back as stored on the mapping.</param>
/// <param name="UserId">The Ticketing user id the agent resolved to.</param>
/// <param name="UserName">That user's account name.</param>
/// <param name="DisplayName">That user's display name.</param>
/// <param name="Roles">The user's Ticketing roles.</param>
/// <param name="DepartmentIds">The departments the user is a member of.</param>
/// <param name="ConversationId">The conversation, echoed back, when one was supplied.</param>
/// <param name="TicketId">The ticket that conversation belongs to, when one was supplied.</param>
/// <param name="TicketNumber">That ticket's number.</param>
/// <param name="TicketInteractionId">The interaction that now records the handler.</param>
/// <param name="HandledByUserId">The Ticketing user recorded as the interaction's handler — the first agent to be resolved on it, which may differ from <paramref name="UserId"/> after a transfer.</param>
public sealed record GenesysAgentContextResponse(
    string Outcome,
    string GenesysUserId,
    Guid UserId,
    string? UserName,
    string? DisplayName,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<int> DepartmentIds,
    string? ConversationId,
    long? TicketId,
    string? TicketNumber,
    long? TicketInteractionId,
    Guid? HandledByUserId);

/// <summary>
/// Translates the transport records above into the normalized application
/// contracts. The one place Genesys' vocabulary is interpreted — an
/// unrecognized channel or event is rejected here with a clear message,
/// never guessed at deeper in the system.
/// </summary>
internal static class GenesysContractMapper
{
    internal static bool TryMap(GenesysInquiryRequest request, out GenesysInquiryDto inquiry, out string? error)
    {
        inquiry = null!;
        error = null;

        if (!TryParseChannel(request.Channel, out var channel))
        {
            error = $"Unsupported channel '{request.Channel}'. Expected Phone, LiveChat (or WebsiteChat / WebMessaging), WhatsApp or SocialMedia.";
            return false;
        }

        inquiry = new GenesysInquiryDto(
            request.ConversationId,
            channel,
            Absent(request.InteractionId),
            Absent(request.ParticipantId),
            Absent(request.CommunicationId),
            Absent(request.Direction),
            Absent(request.CustomerPhone),
            Absent(request.CustomerName),
            Absent(request.CustomerEmail),
            Absent(request.CalledNumber),
            Absent(request.QueueId),
            Absent(request.QueueName),
            Absent(request.AgentId),
            Absent(request.AgentName),
            request.StartedAtUtc,
            request.DepartmentId,
            Absent(request.DepartmentCode),
            Absent(request.TowerName),
            Absent(request.UnitNumber),
            Absent(request.Subject));
        return true;
    }

    /// <summary>
    /// A blank optional value means "not supplied". A Genesys data action
    /// cannot leave a field out of its request template, so an unset
    /// Architect variable arrives as "" — which must never win over a value
    /// TigerCS found itself (a CRM customer name) or be parsed as a
    /// vocabulary word (a handoff mode).
    /// </summary>
    private static string? Absent(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// The channel names Genesys may use for the one Live Chat channel, beside
    /// the normalized enum names themselves. "LiveChat" is the name the
    /// business and the channel catalogue use (<c>LIVE_CHAT</c>);
    /// "WebMessaging" is what Genesys Cloud calls its web chat. Both are the
    /// same channel as <see cref="GenesysChannel.WebsiteChat"/> — an alias,
    /// never a second channel.
    /// </summary>
    private static readonly Dictionary<string, GenesysChannel> ChannelAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LiveChat"] = GenesysChannel.WebsiteChat,
        ["WebMessaging"] = GenesysChannel.WebsiteChat
    };

    internal static bool TryParseChannel(string? value, out GenesysChannel channel)
    {
        var name = value?.Trim() ?? string.Empty;
        if (ChannelAliases.TryGetValue(name, out channel))
        {
            return true;
        }

        return Enum.TryParse(name, ignoreCase: true, out channel) && Enum.IsDefined(channel);
    }

    internal static GenesysTicketUpdateDto Map(GenesysTicketUpdateRequest request) => new(
        request.ConversationId,
        Absent(request.AgentId),
        Absent(request.AgentName),
        request.Ended is null
            ? null
            : new GenesysConversationEndUpdateDto(
                request.Ended.EndedAtUtc,
                Absent(request.Ended.EndReason),
                request.Ended.Transcript?
                    .Select(m => new GenesysTranscriptMessageDto(m.Sender, m.SentAtUtc, m.Body, m.SenderName, m.SenderId, m.ExternalMessageId))
                    .ToList()),
        request.Handoff is null
            ? null
            : new GenesysHandoffUpdateDto(
                request.Handoff.Required,
                request.Handoff.AgentAvailable,
                Absent(request.Handoff.Mode),
                Absent(request.Handoff.Reason),
                Absent(request.Handoff.Trigger),
                Absent(request.Handoff.WorkItemId),
                Absent(request.Handoff.AssignedAgentId)),
        request.StartedAtUtc,
        request.Routing is null
            ? null
            : new GenesysRoutingUpdateDto(
                Absent(request.Routing.QueueId), Absent(request.Routing.QueueName),
                Absent(request.Routing.AgentId), Absent(request.Routing.AgentName)));

    internal static GenesysAgentContextDto Map(GenesysAgentContextRequest request) =>
        new(request.GenesysUserId, request.AgentEmail, request.ConversationId);

    internal static GenesysConversationEndDto Map(GenesysConversationEndRequest request) => new(
        request.ConversationId,
        request.EndedAtUtc,
        request.EndReason,
        request.AgentId,
        request.AgentName,
        request.Transcript?
            .Select(m => new GenesysTranscriptMessageDto(m.Sender, m.SentAtUtc, m.Body, m.SenderName, m.SenderId, m.ExternalMessageId))
            .ToList());
}

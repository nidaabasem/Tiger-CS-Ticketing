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
/// and nothing more. When the Genesys team supplies their real contract, the
/// adapter that translates it lives at this boundary — this record changes
/// or gains a sibling, and nothing behind it moves.
/// </para>
/// </summary>
/// <param name="ConversationId">Required. Genesys' conversation id — the idempotency key: the same value always resolves to the same ticket.</param>
/// <param name="Channel">Required. One of "Phone", "WebsiteChat", "WhatsApp", "SocialMedia" (case-insensitive).</param>
/// <param name="Event">Required. One of "Ringing", "Answered", "Started" (case-insensitive). "Ringing" is accepted and deliberately creates no ticket.</param>
/// <param name="InteractionId">Genesys' interaction id, where it differs from the conversation id.</param>
/// <param name="ParticipantId">Genesys' customer-participant id, where available.</param>
/// <param name="CommunicationId">Genesys' communication id, where available.</param>
/// <param name="Direction">"Inbound"/"Outbound", where available.</param>
/// <param name="CustomerPhone">The customer's mobile number — what the existing TigerCS customer lookup searches with.</param>
/// <param name="CustomerName">The customer's name, where the channel collected one (e.g. the website chat form).</param>
/// <param name="CustomerEmail">The customer's email, where the channel collected one.</param>
/// <param name="CalledNumber">The Tiger number the customer dialed. Recorded, never used for routing.</param>
/// <param name="QueueId">The Genesys queue — resolved to a department via the configured queue mapping when the customer chose no department.</param>
/// <param name="QueueName">The queue's display name, where available.</param>
/// <param name="AgentId">The handling Genesys agent, where available.</param>
/// <param name="AgentName">The handling agent's display name, where available.</param>
/// <param name="StartedAtUtc">When the interaction started, UTC.</param>
/// <param name="DepartmentId">The department the customer explicitly selected (website chat). Wins over the queue mapping.</param>
/// <param name="DepartmentCode">The same explicit selection as a TigerCS department code, for a caller that knows codes rather than ids.</param>
/// <param name="TowerName">The tower/project the customer typed into the chat form, where collected.</param>
/// <param name="UnitNumber">The unit number the customer typed into the chat form, where collected.</param>
/// <param name="Subject">A short subject the channel collected, where available — becomes the ticket's request summary.</param>
public sealed record GenesysInquiryRequest(
    string ConversationId,
    string Channel,
    string Event,
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

        if (!Enum.TryParse<GenesysChannel>(request.Channel, ignoreCase: true, out var channel) || !Enum.IsDefined(channel))
        {
            error = $"Unsupported channel '{request.Channel}'. Expected Phone, WebsiteChat, WhatsApp or SocialMedia.";
            return false;
        }

        if (!Enum.TryParse<GenesysInquiryEvent>(request.Event, ignoreCase: true, out var inquiryEvent) || !Enum.IsDefined(inquiryEvent))
        {
            error = $"Unsupported event '{request.Event}'. Expected Ringing, Answered or Started.";
            return false;
        }

        inquiry = new GenesysInquiryDto(
            request.ConversationId,
            channel,
            inquiryEvent,
            request.InteractionId,
            request.ParticipantId,
            request.CommunicationId,
            request.Direction,
            request.CustomerPhone,
            request.CustomerName,
            request.CustomerEmail,
            request.CalledNumber,
            request.QueueId,
            request.QueueName,
            request.AgentId,
            request.AgentName,
            request.StartedAtUtc,
            request.DepartmentId,
            request.DepartmentCode,
            request.TowerName,
            request.UnitNumber,
            request.Subject);
        return true;
    }

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

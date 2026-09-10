namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>Who authored one transcript message of an interaction.</summary>
public enum InteractionMessageSender : byte
{
    Customer = 1,

    /// <summary>A human agent speaking to the customer — distinct from <see cref="VirtualAgent"/>, so an agent reading a chatbot conversation can tell which lines a person wrote.</summary>
    HumanAgent = 2,

    /// <summary>A platform/bot/system line in the transcript (e.g. "Conversation transferred", an automated greeting) — kept so the record is complete, rendered distinctly.</summary>
    System = 3,

    /// <summary>
    /// A virtual agent / chatbot speaking to the customer. Distinct from
    /// <see cref="HumanAgent"/> because an agent taking over a bot conversation
    /// must be able to see which lines a human said and which the bot did —
    /// and distinct from <see cref="System"/> because a bot asking "Are you
    /// asking about an NOC for resale?" is conversation, not platform noise.
    /// A TigerCS-owned normalized value; no Genesys vocabulary is assumed.
    /// </summary>
    VirtualAgent = 4
}

/// <summary>
/// One message of a text conversation's transcript (website chat, WhatsApp,
/// social media chat), belonging to exactly one <see cref="TicketInteraction"/>
/// (Genesys integration phase 1). Structured on purpose — one row per
/// message with sender, timestamp and body — rather than one display blob,
/// so Ticket Details can render "Customer: … / Agent: …" in order and later
/// reporting can count/measure exchanges; <see cref="Sequence"/> is the
/// authoritative order (the position in the transcript as delivered), with
/// <see cref="SentAtUtc"/> as the displayed time, because two messages may
/// share a timestamp and a provider clock is not guaranteed monotonic.
/// Append-only; never edited or deleted. Voice calls have no rows here.
/// </summary>
public class TicketInteractionMessage
{
    public const int SenderNameMaxLength = 200;
    public const int SenderIdMaxLength = 64;
    public const int ExternalMessageIdMaxLength = 64;

    public long TicketInteractionMessageId { get; private set; }
    public long TicketInteractionId { get; private set; }

    /// <summary>1-based position within the interaction's transcript — unique per interaction, the authoritative ordering.</summary>
    public int Sequence { get; private set; }

    public InteractionMessageSender Sender { get; private set; }

    /// <summary>The sender's display name as the channel reported it (the customer's name, the agent's name), where available.</summary>
    public string? SenderName { get; private set; }

    /// <summary>The sender's identifier on the Genesys side (participant/agent id), where available — an external identifier, never a foreign key.</summary>
    public string? SenderId { get; private set; }

    /// <summary>Genesys' own id for the message, where provided — stored for traceability and for a future incremental-append path to dedupe on.</summary>
    public string? ExternalMessageId { get; private set; }

    public DateTime SentAtUtc { get; private set; }

    /// <summary>The message text, verbatim. Empty bodies are rejected: a transcript line with nothing said is a data error, not a message.</summary>
    public string Body { get; private set; } = string.Empty;

    private TicketInteractionMessage() { }

    public TicketInteractionMessage(
        long ticketInteractionId,
        int sequence,
        InteractionMessageSender sender,
        string? senderName,
        string? senderId,
        DateTime sentAtUtc,
        string body,
        string? externalMessageId = null)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Sequence is 1-based.");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("Body is required.", nameof(body));
        }

        if (!Enum.IsDefined(sender))
        {
            throw new ArgumentOutOfRangeException(nameof(sender), sender, "Unknown message sender.");
        }

        TicketInteractionId = ticketInteractionId;
        Sequence = sequence;
        Sender = sender;
        SenderName = Trim(senderName, SenderNameMaxLength);
        SenderId = Trim(senderId, SenderIdMaxLength);
        ExternalMessageId = Trim(externalMessageId, ExternalMessageIdMaxLength);
        SentAtUtc = sentAtUtc;
        Body = body;
    }

    private static string? Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}

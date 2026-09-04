namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// One customer interaction associated with a ticket — a ticket accumulates
/// <b>many</b> of these over its lifetime (the original inbound Genesys
/// call, a follow-up inbound call, an outbound call, another Genesys
/// conversation, a future WhatsApp exchange, a Face-to-Face follow-up), each
/// independently retaining its source, channel, customer phone, and — where
/// Genesys handled it — the Genesys context, verbatim. Persisted for audit,
/// history, reporting, and Ticket ↔ Genesys conversation traceability. This
/// is a <b>record of what Genesys (or the agent) said</b>, never routing
/// logic: Ticketing does not reproduce Called Number → Queue mapping, and
/// none of this data drives department routing inside Ticketing
/// (Category/Request Type do that, exactly as before).
///
/// <para>
/// <b>Exactly one interaction per ticket is the originating one</b>
/// (<see cref="IsOriginatingInteraction"/>) — the interaction the ticket
/// was created from, written by ticket creation in the same transaction. A
/// filtered unique index enforces at-most-one at the database. Later
/// interactions (recorded by future phases) are appended with the flag
/// false and never touch the originating row.
/// </para>
///
/// <para>
/// <b>Every Genesys field is nullable by design.</b> The exact Genesys API
/// contract is not finalized; the fields below are the context Genesys is
/// expected to provide (channel, customer phone, called/destination number,
/// queue id/name, agent id/name, conversation id, interaction start,
/// direction). Face-to-Face / walk-in interactions never have any of them —
/// <see cref="InteractionContextSource.Ticketing"/> rows enforce that they
/// stay null. Only the conversation id is required for a
/// <see cref="InteractionContextSource.Genesys"/> row, because without it
/// the row cannot link back to the interaction at all.
/// </para>
///
/// <para>
/// <b>CustomerPhone here is the customer's identity input</b> (the number
/// CRM/PACT/Tasleeh verification searches with), distinct from
/// <see cref="CalledNumber"/> — the Tiger number the customer dialed,
/// meaningful only on the Genesys side. Genesys identifiers are external
/// identifiers stored as strings, never foreign keys, and are for
/// audit/support/integration surfaces — not for prominent display in the
/// main CS UI.
/// </para>
/// </summary>
public class TicketInteraction
{
    public long TicketInteractionId { get; private set; }
    public long TicketId { get; private set; }

    /// <summary>True on the one interaction the ticket was created from — at most one per ticket (filtered unique index). Set at construction, never mutated.</summary>
    public bool IsOriginatingInteraction { get; private set; }

    public InteractionContextSource Source { get; private set; }
    public Channel ChannelId { get; private set; }

    /// <summary>The customer's phone number for this interaction — the identity input for customer verification, preserved per interaction for reporting.</summary>
    public string CustomerPhone { get; private set; } = string.Empty;

    /// <summary>The company/destination number the interaction arrived on (Genesys side), or null — never used by Ticketing for routing.</summary>
    public string? CalledNumber { get; private set; }

    public string? GenesysConversationId { get; private set; }
    public string? GenesysQueueId { get; private set; }
    public string? GenesysQueueName { get; private set; }
    public string? GenesysAgentId { get; private set; }
    public string? GenesysAgentName { get; private set; }

    /// <summary>When the interaction started on the Genesys side, where provided — may precede ticket creation.</summary>
    public DateTime? InteractionStartedAtUtc { get; private set; }

    /// <summary>Interaction direction as reported by Genesys (e.g. "Inbound"), where available. Free text until the integration contract fixes an enumeration.</summary>
    public string? Direction { get; private set; }

    /// <summary>When this row was recorded by Ticketing (creation/audit timestamp), as distinct from <see cref="InteractionStartedAtUtc"/> — Genesys' own clock.</summary>
    public DateTime CreatedAtUtc { get; private set; }

    private TicketInteraction() { }

    private TicketInteraction(
        long ticketId, bool isOriginatingInteraction, InteractionContextSource source,
        Channel channelId, string customerPhone, DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(customerPhone))
        {
            throw new ArgumentException(
                "CustomerPhone is required — it is the identity input customer verification searches with.", nameof(customerPhone));
        }

        TicketId = ticketId;
        IsOriginatingInteraction = isOriginatingInteraction;
        Source = source;
        ChannelId = channelId;
        CustomerPhone = customerPhone;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>A Face-to-Face / locally-created interaction: channel and phone the agent entered; every Genesys field stays null, by construction.</summary>
    public static TicketInteraction CreateLocal(
        long ticketId, Channel channelId, string customerPhone, DateTime createdAtUtc, bool isOriginatingInteraction = false) =>
        new(ticketId, isOriginatingInteraction, InteractionContextSource.Ticketing, channelId, customerPhone, createdAtUtc);

    /// <summary>
    /// A Genesys-provided interaction. Only the conversation id is mandatory —
    /// every other field is optional until the Genesys API contract is
    /// finalized, and absent values are stored as null rather than guessed.
    /// </summary>
    public static TicketInteraction CreateFromGenesys(
        long ticketId,
        Channel channelId,
        string customerPhone,
        string genesysConversationId,
        string? calledNumber,
        string? genesysQueueId,
        string? genesysQueueName,
        string? genesysAgentId,
        string? genesysAgentName,
        DateTime? interactionStartedAtUtc,
        string? direction,
        DateTime createdAtUtc,
        bool isOriginatingInteraction = false)
    {
        if (string.IsNullOrWhiteSpace(genesysConversationId))
        {
            throw new ArgumentException(
                "GenesysConversationId is required for a Genesys-sourced interaction — without it the row cannot link back to the conversation.",
                nameof(genesysConversationId));
        }

        return new TicketInteraction(
            ticketId, isOriginatingInteraction, InteractionContextSource.Genesys, channelId, customerPhone, createdAtUtc)
        {
            GenesysConversationId = genesysConversationId,
            CalledNumber = calledNumber,
            GenesysQueueId = genesysQueueId,
            GenesysQueueName = genesysQueueName,
            GenesysAgentId = genesysAgentId,
            GenesysAgentName = genesysAgentName,
            InteractionStartedAtUtc = interactionStartedAtUtc,
            Direction = direction
        };
    }
}

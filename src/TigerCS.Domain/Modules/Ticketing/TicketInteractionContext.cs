namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// The interaction context a ticket was created from — one optional row per
/// ticket (Workflow/Automation phase 2), persisted for audit, history,
/// reporting, and Ticket ↔ Genesys conversation traceability. This is a
/// <b>record of what Genesys (or the agent) said</b>, never routing logic:
/// Ticketing does not reproduce Called Number → Queue mapping, and none of
/// this data drives department routing inside Ticketing (Category/Request
/// Type do that, exactly as before).
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
/// CRM/PACT/Tasleeh verification searches with, copied from the intake),
/// distinct from <see cref="CalledNumber"/> — the Tiger number the customer
/// dialed, meaningful only on the Genesys side. Genesys identifiers are
/// external identifiers stored as strings, never foreign keys, and are for
/// audit/support/integration surfaces — not for prominent display in the
/// main CS UI.
/// </para>
/// </summary>
public class TicketInteractionContext
{
    /// <summary>Also the primary key — at most one context per ticket.</summary>
    public long TicketId { get; private set; }

    public InteractionContextSource Source { get; private set; }
    public Channel ChannelId { get; private set; }

    /// <summary>The customer's phone number as captured at intake — the identity input for customer verification, preserved here for interaction reporting.</summary>
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

    public DateTime CreatedAtUtc { get; private set; }

    private TicketInteractionContext() { }

    private TicketInteractionContext(long ticketId, InteractionContextSource source, Channel channelId, string customerPhone, DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(customerPhone))
        {
            throw new ArgumentException(
                "CustomerPhone is required — it is the identity input customer verification searches with.", nameof(customerPhone));
        }

        TicketId = ticketId;
        Source = source;
        ChannelId = channelId;
        CustomerPhone = customerPhone;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>The Face-to-Face / locally-created context: channel and phone the agent entered; every Genesys field stays null, by construction.</summary>
    public static TicketInteractionContext CreateLocal(long ticketId, Channel channelId, string customerPhone, DateTime createdAtUtc) =>
        new(ticketId, InteractionContextSource.Ticketing, channelId, customerPhone, createdAtUtc);

    /// <summary>
    /// A Genesys-provided context. Only the conversation id is mandatory —
    /// every other field is optional until the Genesys API contract is
    /// finalized, and absent values are stored as null rather than guessed.
    /// </summary>
    public static TicketInteractionContext CreateFromGenesys(
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
        DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(genesysConversationId))
        {
            throw new ArgumentException(
                "GenesysConversationId is required for a Genesys-sourced context — without it the ticket cannot link back to the interaction.",
                nameof(genesysConversationId));
        }

        var context = new TicketInteractionContext(ticketId, InteractionContextSource.Genesys, channelId, customerPhone, createdAtUtc)
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
        return context;
    }
}

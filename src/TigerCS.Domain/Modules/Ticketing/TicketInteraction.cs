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
/// <b>One Genesys conversation is exactly one interaction, on exactly one
/// ticket</b> (Genesys integration phase 1). <see cref="GenesysConversationId"/>
/// is unique across the whole table (filtered unique index), which is the
/// database-level half of "a retried or duplicated Genesys event never
/// creates a second ticket": the application checks first, and a lost race
/// surfaces as a unique-constraint violation the caller treats as "already
/// ingested". The conversation's lifecycle is recorded here too —
/// <see cref="EndedAtUtc"/>/<see cref="EndReason"/> via <see cref="End"/> —
/// and its transcript lives in <see cref="TicketInteractionMessage"/> rows
/// keyed by this interaction. Ending an interaction never changes the
/// ticket's own status: the chat ends, the NOC workflow continues.
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
    public const int CustomerNameMaxLength = 200;
    public const int CustomerEmailMaxLength = 256;
    public const int EndReasonMaxLength = 100;
    public const int GenesysAgentUserIdMaxLength = 64;

    public long TicketInteractionId { get; private set; }
    public long TicketId { get; private set; }

    /// <summary>True on the one interaction the ticket was created from — at most one per ticket (filtered unique index). Set at construction, never mutated.</summary>
    public bool IsOriginatingInteraction { get; private set; }

    public InteractionContextSource Source { get; private set; }
    /// <summary>The <see cref="Channel"/> this interaction happened on. On the originating interaction, this is the ticket's originating channel.</summary>
    public byte ChannelId { get; private set; }

    /// <summary>The customer's phone number for this interaction — the identity input for customer verification, preserved per interaction for reporting.</summary>
    public string CustomerPhone { get; private set; } = string.Empty;

    /// <summary>The customer's name as the channel reported it (e.g. the website chat form's Full Name), where available. Display/audit only — never a verified identity.</summary>
    public string? CustomerName { get; private set; }

    /// <summary>The customer's email as the channel reported it, where available. Display/audit only — never a verified identity.</summary>
    public string? CustomerEmail { get; private set; }

    /// <summary>The company/destination number the interaction arrived on (Genesys side), or null — never used by Ticketing for routing.</summary>
    public string? CalledNumber { get; private set; }

    public string? GenesysConversationId { get; private set; }
    public string? GenesysQueueId { get; private set; }
    public string? GenesysQueueName { get; private set; }
    public string? GenesysAgentId { get; private set; }
    public string? GenesysAgentName { get; private set; }

    /// <summary>
    /// Interaction ownership (Genesys agent identity mapping): the immutable
    /// Genesys User ID of the agent that handled this interaction, exactly as
    /// Genesys sent it — recorded only once that id has been resolved to a
    /// Ticketing user (<see cref="HandledByUserId"/>), so the pair is always
    /// written together. Distinct from <see cref="GenesysAgentId"/>, which
    /// is the verbatim (unresolved) agent context Genesys reported.
    /// </summary>
    public string? GenesysAgentUserId { get; private set; }

    /// <summary>
    /// The Ticketing user (<c>AspNetUsers.Id</c>) that handled this
    /// interaction, resolved <b>server-side</b> from
    /// <see cref="GenesysAgentUserId"/> via <c>AspNetUsers.GenesysUserId</c>
    /// — never taken from a client. Null for historical rows, for
    /// system-generated interactions, and for a Genesys agent that is not
    /// mapped to a Ticketing user. This is "who handled the interaction";
    /// it is deliberately <b>not</b> the ticket's assignee/current owner,
    /// which lives on the <see cref="Ticket"/> and moves through its own
    /// assignment rules.
    /// </summary>
    public Guid? HandledByUserId { get; private set; }

    /// <summary>When the interaction started on the Genesys side, where provided — may precede ticket creation.</summary>
    public DateTime? InteractionStartedAtUtc { get; private set; }

    /// <summary>Interaction direction as reported by Genesys (e.g. "Inbound"), where available. Free text until the integration contract fixes an enumeration.</summary>
    public string? Direction { get; private set; }

    /// <summary>When the conversation ended/disconnected (Genesys' clock where it supplies one), or null while the interaction is still live or was never reported ended.</summary>
    public DateTime? EndedAtUtc { get; private set; }

    /// <summary>Why the conversation ended, as reported (e.g. "AgentDisconnect", "CustomerDisconnect", "Timeout") — free text until the Genesys contract fixes an enumeration; null when not reported.</summary>
    public string? EndReason { get; private set; }

    /// <summary>When this row was recorded by Ticketing (creation/audit timestamp), as distinct from <see cref="InteractionStartedAtUtc"/> — Genesys' own clock.</summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Whether <see cref="End"/> has been recorded. Derived — the interaction's own lifecycle, independent of the ticket's <see cref="TicketStatus"/>.</summary>
    public bool IsEnded => EndedAtUtc is not null;

    private TicketInteraction() { }

    private TicketInteraction(
        long ticketId, bool isOriginatingInteraction, InteractionContextSource source,
        byte channelId, string? customerPhone, DateTime createdAtUtc)
    {
        if (channelId == 0)
        {
            throw new ArgumentException("ChannelId is required.", nameof(channelId));
        }

        // The phone is the identity input customer verification searches
        // with, but whether one MUST exist is the channel's RequiresPhone
        // configuration (enforced at intake) — a Face-to-Face / kiosk
        // interaction may legitimately carry none, stored as empty.

        TicketId = ticketId;
        IsOriginatingInteraction = isOriginatingInteraction;
        Source = source;
        ChannelId = channelId;
        CustomerPhone = customerPhone ?? string.Empty;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>A Face-to-Face / locally-created interaction: channel and phone the agent entered; every Genesys field stays null, by construction.</summary>
    public static TicketInteraction CreateLocal(
        long ticketId, byte channelId, string? customerPhone, DateTime createdAtUtc, bool isOriginatingInteraction = false) =>
        new(ticketId, isOriginatingInteraction, InteractionContextSource.Ticketing, channelId, customerPhone, createdAtUtc);

    /// <summary>
    /// A Genesys-provided interaction. Only the conversation id is mandatory —
    /// every other field is optional until the Genesys API contract is
    /// finalized, and absent values are stored as null rather than guessed.
    /// </summary>
    public static TicketInteraction CreateFromGenesys(
        long ticketId,
        byte channelId,
        string? customerPhone,
        string genesysConversationId,
        string? calledNumber,
        string? genesysQueueId,
        string? genesysQueueName,
        string? genesysAgentId,
        string? genesysAgentName,
        DateTime? interactionStartedAtUtc,
        string? direction,
        DateTime createdAtUtc,
        bool isOriginatingInteraction = false,
        string? customerName = null,
        string? customerEmail = null)
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
            GenesysConversationId = genesysConversationId.Trim(),
            CalledNumber = calledNumber,
            GenesysQueueId = genesysQueueId,
            GenesysQueueName = genesysQueueName,
            GenesysAgentId = genesysAgentId,
            GenesysAgentName = genesysAgentName,
            InteractionStartedAtUtc = interactionStartedAtUtc,
            Direction = direction,
            CustomerName = Truncate(customerName, CustomerNameMaxLength),
            CustomerEmail = Truncate(customerEmail, CustomerEmailMaxLength)
        };
    }

    /// <summary>
    /// Records that the conversation ended or was disconnected — for any
    /// reason: the agent ended it, the customer closed the browser, the
    /// connection dropped, Genesys timed it out. Write-once: a redelivered
    /// end event for an already-ended interaction is refused here
    /// (<see cref="TicketInteractionAlreadyEndedException"/>) so the caller
    /// can treat it as "already processed" rather than moving the recorded
    /// end time. Deliberately touches nothing on the <see cref="Ticket"/>:
    /// an ended chat is not a closed ticket — the ticket keeps following the
    /// existing TigerCS workflow.
    /// </summary>
    public void End(DateTime endedAtUtc, string? endReason)
    {
        if (EndedAtUtc is { } already)
        {
            throw new TicketInteractionAlreadyEndedException(TicketInteractionId, already);
        }

        EndedAtUtc = endedAtUtc;
        EndReason = Truncate(endReason, EndReasonMaxLength);
    }

    /// <summary>
    /// Fills in the handling agent when a later event names one and the
    /// interaction does not yet know it (the start event of a queued chat
    /// often precedes agent assignment) — apply-if-absent, so an agent
    /// recorded at creation is never overwritten by a later, different value.
    /// </summary>
    public void RecordAgentIfAbsent(string? genesysAgentId, string? genesysAgentName)
    {
        if (GenesysAgentId is null && !string.IsNullOrWhiteSpace(genesysAgentId))
        {
            GenesysAgentId = genesysAgentId;
        }

        if (GenesysAgentName is null && !string.IsNullOrWhiteSpace(genesysAgentName))
        {
            GenesysAgentName = genesysAgentName;
        }
    }

    /// <summary>
    /// Records which Ticketing user handled this interaction, as resolved
    /// server-side from the Genesys User ID. Apply-if-absent, mirroring
    /// <see cref="RecordAgentIfAbsent"/>: the first resolved agent is the
    /// one recorded, and a later, different agent never overwrites it.
    /// Returns true when the ownership was written by this call, false when
    /// it was already recorded. Both values are required together — a
    /// Genesys agent id without its resolved user (or the reverse) is never
    /// stored, because the pair is what makes the row auditable.
    /// </summary>
    public bool RecordHandlingAgentIfAbsent(string genesysAgentUserId, Guid handledByUserId)
    {
        if (string.IsNullOrWhiteSpace(genesysAgentUserId))
        {
            throw new ArgumentException("GenesysAgentUserId is required to record interaction ownership.", nameof(genesysAgentUserId));
        }

        if (handledByUserId == Guid.Empty)
        {
            throw new ArgumentException("HandledByUserId must be a resolved Ticketing user id.", nameof(handledByUserId));
        }

        if (HandledByUserId is not null)
        {
            return false;
        }

        GenesysAgentUserId = Truncate(genesysAgentUserId, GenesysAgentUserIdMaxLength);
        HandledByUserId = handledByUserId;
        return true;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}

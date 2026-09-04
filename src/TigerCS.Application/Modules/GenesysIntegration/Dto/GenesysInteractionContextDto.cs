namespace TigerCS.Application.Modules.GenesysIntegration.Dto;

/// <summary>
/// The integration boundary for Genesys-provided interaction context
/// (Workflow/Automation phase 2). This DTO is the contract <b>Ticketing</b>
/// needs — the fields Genesys is expected to hand over once an interaction
/// is routed — deliberately defined here, on Ticketing's side of the
/// boundary, because the exact Genesys API contracts are not finalized:
/// nothing in this type assumes an endpoint shape, an auth model, or a
/// payload encoding, and no Genesys API client exists in this phase.
///
/// <para>
/// <b>Everything except the conversation id is optional</b>, because
/// Genesys' guarantees per channel are not confirmed — absent values are
/// stored as null, never guessed. Interaction routing itself (Called Number
/// → Queue, queue selection, agent selection) stays entirely inside Genesys;
/// Ticketing persists this context verbatim for audit/traceability
/// (<c>TicketInteractionContext</c>) and never re-derives routing from it.
/// </para>
///
/// <para>
/// <b>Expected to be provided by Genesys later</b> (per the operational
/// design; to be confirmed with the finalized integration contract):
/// channel, customer phone, called/destination number, queue id+name, agent
/// id+name, conversation/interaction id, interaction start time, and
/// direction where available. The customer phone still arrives through the
/// existing intake (it is the verification identity input); when Genesys
/// also supplies it, the intake value remains authoritative for
/// verification. A future reverse direction (Ticketing → Genesys: ticket
/// number/id, department, request type, status) is out of scope and only
/// noted so this boundary stays extensible.
/// </para>
/// </summary>
/// <param name="ConversationId">Required. Genesys' conversation/interaction id — the traceability link between a Ticket and the interaction.</param>
/// <param name="CalledNumber">The company/destination number the customer reached (Genesys-side datum; never used by Ticketing for routing).</param>
/// <param name="QueueId">The Genesys queue id the interaction was routed to, where provided.</param>
/// <param name="QueueName">The Genesys queue display name, where provided.</param>
/// <param name="AgentId">The Genesys agent id that handled the interaction, where provided.</param>
/// <param name="AgentName">The Genesys agent display name, where provided.</param>
/// <param name="InteractionStartedAtUtc">When the interaction started on the Genesys side (UTC), where provided.</param>
/// <param name="Direction">Interaction direction as reported by Genesys (e.g. "Inbound"), where available.</param>
public sealed record GenesysInteractionContextDto(
    string ConversationId,
    string? CalledNumber = null,
    string? QueueId = null,
    string? QueueName = null,
    string? AgentId = null,
    string? AgentName = null,
    DateTime? InteractionStartedAtUtc = null,
    string? Direction = null);

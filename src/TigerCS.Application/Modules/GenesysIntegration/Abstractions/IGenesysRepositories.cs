using TigerCS.Domain.Modules.GenesysIntegration;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Abstractions;

/// <summary>Genesys Queue → Department mapping configuration (rows an administrator enters; no queue id is ever hard-coded).</summary>
public interface IGenesysQueueMappingRepository
{
    /// <summary>The ACTIVE mapping for one Genesys queue id (case-insensitive), or null when the queue is unmapped.</summary>
    Task<GenesysQueueMapping?> GetActiveByQueueIdAsync(string queueId, CancellationToken cancellationToken = default);

    Task<GenesysQueueMapping?> GetByIdAsync(int genesysQueueMappingId, CancellationToken cancellationToken = default);

    /// <summary>Every mapping, ordered by queue id — the administration listing.</summary>
    Task<IReadOnlyList<GenesysQueueMapping>> ListAsync(bool includeInactive, CancellationToken cancellationToken = default);

    /// <summary>Case-insensitive queue-id uniqueness check, optionally excluding the mapping being edited.</summary>
    Task<bool> QueueIdExistsAsync(string queueId, int? excludeMappingId, CancellationToken cancellationToken = default);

    Task AddAsync(GenesysQueueMapping mapping, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads and writes over a Genesys conversation's interaction — the
/// conversation-scoped half of <c>ITicketInteractionRepository</c> (which
/// stays ticket-scoped). Kept a separate port so the ticket-creation path is
/// untouched by this phase's additions.
/// </summary>
public interface IGenesysConversationRepository
{
    /// <summary>The interaction recorded for one Genesys conversation id, or null when the conversation was never ingested. The uniqueness this relies on is a database index, not a convention.</summary>
    Task<TicketInteraction?> GetByConversationIdAsync(string genesysConversationId, CancellationToken cancellationToken = default);

    /// <summary>Every transcript message of one interaction, in <c>Sequence</c> order.</summary>
    Task<IReadOnlyList<TicketInteractionMessage>> ListMessagesAsync(long ticketInteractionId, CancellationToken cancellationToken = default);

    /// <summary>Transcript messages for several interactions at once — one query for a whole ticket's conversation history, never one per interaction.</summary>
    Task<IReadOnlyDictionary<long, IReadOnlyList<TicketInteractionMessage>>> ListMessagesForInteractionsAsync(
        IReadOnlyCollection<long> ticketInteractionIds, CancellationToken cancellationToken = default);

    /// <summary>How many transcript messages an interaction already has — the append point for a transcript delivered in parts.</summary>
    Task<int> CountMessagesAsync(long ticketInteractionId, CancellationToken cancellationToken = default);

    Task AddMessageAsync(TicketInteractionMessage message, CancellationToken cancellationToken = default);
}

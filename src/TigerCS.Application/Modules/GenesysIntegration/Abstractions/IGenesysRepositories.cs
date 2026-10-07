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

    /// <summary>One interaction by its own id (tracked, so the caller can change it), or null.</summary>
    Task<TicketInteraction?> GetByIdAsync(long ticketInteractionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ids of interactions that have been waiting on a customer reply since
    /// before <paramref name="cutoffUtc"/> (exclusive), oldest first, at most
    /// <paramref name="batchSize"/>. A candidate list only — the closing
    /// service re-reads and re-checks each one before acting.
    /// </summary>
    Task<IReadOnlyList<long>> ListAwaitingReplyOlderThanAsync(DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default);

    /// <summary>Every transcript message of one interaction, in <c>Sequence</c> order.</summary>
    Task<IReadOnlyList<TicketInteractionMessage>> ListMessagesAsync(long ticketInteractionId, CancellationToken cancellationToken = default);

    /// <summary>Transcript messages for several interactions at once — one query for a whole ticket's conversation history, never one per interaction.</summary>
    Task<IReadOnlyDictionary<long, IReadOnlyList<TicketInteractionMessage>>> ListMessagesForInteractionsAsync(
        IReadOnlyCollection<long> ticketInteractionIds, CancellationToken cancellationToken = default);

    /// <summary>How many transcript messages an interaction already has — the append point for a transcript delivered in parts.</summary>
    Task<int> CountMessagesAsync(long ticketInteractionId, CancellationToken cancellationToken = default);

    Task AddMessageAsync(TicketInteractionMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Genesys agent → Ticketing user mapping (<c>AspNetUsers.GenesysUserId</c>),
/// read-only from the integration's side. Implemented in Infrastructure over
/// Identity's user store; Application never sees the Identity user type.
/// <b>Nothing here creates a user</b> — mapping is an administrative act.
/// </summary>
public interface IGenesysAgentMappingRepository
{
    /// <summary>
    /// The Ticketing user mapped to one Genesys User ID, or null when no user
    /// carries that id. Matches on <c>GenesysUserId</c> only — never on
    /// email, user name or display name — and relies on the filtered unique
    /// index <c>UX_AspNetUsers_GenesysUserId</c> for there being at most one.
    /// </summary>
    Task<Dto.GenesysMappedAgent?> FindByGenesysUserIdAsync(string genesysUserId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Secure Screen Pop launches (<c>GenesysScreenPopLaunches</c>). Looked up
/// by token <b>hash</b> only — the raw token never reaches persistence.
/// Saves on its own because issue and redemption are each one self-contained
/// write (the launch row plus its audit entry, through the same context).
/// </summary>
public interface IGenesysScreenPopLaunchStore
{
    Task AddAsync(GenesysScreenPopLaunch launch, CancellationToken cancellationToken = default);

    Task<GenesysScreenPopLaunch?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists pending changes. Returns false — writing nothing — when a
    /// concurrent redemption of the same launch committed first
    /// (<c>RedeemedAtUtc</c> is a concurrency token), so a token can never be
    /// redeemed twice even under a race.
    /// </summary>
    Task<bool> SaveChangesAsync(CancellationToken cancellationToken = default);
}

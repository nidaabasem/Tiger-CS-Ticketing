using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Domain.Modules.GenesysIntegration;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Fakes;

public sealed class FakeGenesysQueueMappingRepository : IGenesysQueueMappingRepository
{
    private readonly List<GenesysQueueMapping> _mappings = [];
    private int _nextId = 1;

    public IReadOnlyList<GenesysQueueMapping> All => _mappings;

    public GenesysQueueMapping Add(GenesysQueueMapping mapping)
    {
        typeof(GenesysQueueMapping).GetProperty(nameof(GenesysQueueMapping.GenesysQueueMappingId))!
            .SetValue(mapping, _nextId++);
        _mappings.Add(mapping);
        return mapping;
    }

    /// <summary>Maps a queue to a department, active — the common test setup.</summary>
    public GenesysQueueMapping Map(string queueId, int departmentId, bool isActive = true) =>
        Add(new GenesysQueueMapping(queueId, $"Queue {queueId}", departmentId, DateTime.UtcNow, isActive));

    public Task<GenesysQueueMapping?> GetActiveByQueueIdAsync(string queueId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_mappings.FirstOrDefault(m =>
            m.IsActive && string.Equals(m.QueueId, queueId.Trim(), StringComparison.OrdinalIgnoreCase)));

    public Task<GenesysQueueMapping?> GetByIdAsync(int genesysQueueMappingId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_mappings.FirstOrDefault(m => m.GenesysQueueMappingId == genesysQueueMappingId));

    public Task<IReadOnlyList<GenesysQueueMapping>> ListAsync(bool includeInactive, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GenesysQueueMapping>>(
            _mappings.Where(m => includeInactive || m.IsActive).OrderBy(m => m.QueueId, StringComparer.Ordinal).ToList());

    public Task<bool> QueueIdExistsAsync(string queueId, int? excludeMappingId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_mappings.Any(m =>
            string.Equals(m.QueueId, queueId.Trim(), StringComparison.OrdinalIgnoreCase)
            && (excludeMappingId is null || m.GenesysQueueMappingId != excludeMappingId)));

    public Task AddAsync(GenesysQueueMapping mapping, CancellationToken cancellationToken = default)
    {
        Add(mapping);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Conversation-scoped view over the SAME interaction store the ticket
/// creation path writes to (<see cref="FakeTicketInteractionRepository"/>) —
/// deliberately not a second list. If these two diverged, a test could
/// "prove" idempotency against a store nothing actually wrote to.
/// </summary>
public sealed class FakeGenesysConversationRepository(FakeTicketInteractionRepository interactions)
    : IGenesysConversationRepository
{
    private readonly List<TicketInteractionMessage> _messages = [];
    private long _nextId = 1;

    public IReadOnlyList<TicketInteractionMessage> AllMessages => _messages;

    public Task<TicketInteraction?> GetByConversationIdAsync(
        string genesysConversationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(interactions.All.FirstOrDefault(i => i.GenesysConversationId == genesysConversationId.Trim()));

    public Task<IReadOnlyList<TicketInteractionMessage>> ListMessagesAsync(
        long ticketInteractionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TicketInteractionMessage>>(
            _messages.Where(m => m.TicketInteractionId == ticketInteractionId).OrderBy(m => m.Sequence).ToList());

    public Task<IReadOnlyDictionary<long, IReadOnlyList<TicketInteractionMessage>>> ListMessagesForInteractionsAsync(
        IReadOnlyCollection<long> ticketInteractionIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<long, IReadOnlyList<TicketInteractionMessage>>>(
            _messages
                .Where(m => ticketInteractionIds.Contains(m.TicketInteractionId))
                .GroupBy(m => m.TicketInteractionId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<TicketInteractionMessage>)g.OrderBy(m => m.Sequence).ToList()));

    public Task<int> CountMessagesAsync(long ticketInteractionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_messages.Count(m => m.TicketInteractionId == ticketInteractionId));

    public Task AddMessageAsync(TicketInteractionMessage message, CancellationToken cancellationToken = default)
    {
        // Mirror the database's unique (interaction, sequence) index so no
        // test can pass while writing a transcript with a duplicate position.
        if (_messages.Any(m => m.TicketInteractionId == message.TicketInteractionId && m.Sequence == message.Sequence))
        {
            throw new InvalidOperationException(
                $"Interaction {message.TicketInteractionId} already has a message at sequence {message.Sequence}.");
        }

        typeof(TicketInteractionMessage).GetProperty(nameof(TicketInteractionMessage.TicketInteractionMessageId))!
            .SetValue(message, _nextId++);
        _messages.Add(message);
        return Task.CompletedTask;
    }
}

using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Domain.Modules.GenesysIntegration;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.GenesysIntegration.Repositories;

public sealed class GenesysQueueMappingRepository(TigerCsDbContext dbContext) : IGenesysQueueMappingRepository
{
    public Task<GenesysQueueMapping?> GetActiveByQueueIdAsync(string queueId, CancellationToken cancellationToken = default)
    {
        var value = queueId.Trim();
        return dbContext.GenesysQueueMappings.FirstOrDefaultAsync(
            m => m.IsActive && m.QueueId == value, cancellationToken);
    }

    public Task<GenesysQueueMapping?> GetByIdAsync(int genesysQueueMappingId, CancellationToken cancellationToken = default) =>
        dbContext.GenesysQueueMappings.FirstOrDefaultAsync(
            m => m.GenesysQueueMappingId == genesysQueueMappingId, cancellationToken);

    public async Task<IReadOnlyList<GenesysQueueMapping>> ListAsync(bool includeInactive, CancellationToken cancellationToken = default)
    {
        var query = dbContext.GenesysQueueMappings.AsQueryable();
        if (!includeInactive)
        {
            query = query.Where(m => m.IsActive);
        }

        return await query.OrderBy(m => m.QueueId).ToListAsync(cancellationToken);
    }

    public Task<bool> QueueIdExistsAsync(string queueId, int? excludeMappingId, CancellationToken cancellationToken = default)
    {
        var value = queueId.Trim();
        return dbContext.GenesysQueueMappings.AnyAsync(
            m => m.QueueId == value && (excludeMappingId == null || m.GenesysQueueMappingId != excludeMappingId),
            cancellationToken);
    }

    public async Task AddAsync(GenesysQueueMapping mapping, CancellationToken cancellationToken = default) =>
        await dbContext.GenesysQueueMappings.AddAsync(mapping, cancellationToken);
}

/// <summary>
/// Conversation-scoped reads/writes over the interaction and its transcript.
/// The conversation lookup relies on the unique filtered index on
/// <c>TicketInteractions.GenesysConversationId</c> — that index is what makes
/// "one conversation, one interaction" a database fact rather than an
/// assumption this repository would otherwise be making.
/// </summary>
public sealed class GenesysConversationRepository(TigerCsDbContext dbContext) : IGenesysConversationRepository
{
    public Task<TicketInteraction?> GetByConversationIdAsync(
        string genesysConversationId, CancellationToken cancellationToken = default)
    {
        var value = genesysConversationId.Trim();
        return dbContext.TicketInteractions.FirstOrDefaultAsync(
            i => i.GenesysConversationId == value, cancellationToken);
    }

    public async Task<IReadOnlyList<TicketInteractionMessage>> ListMessagesAsync(
        long ticketInteractionId, CancellationToken cancellationToken = default) =>
        await dbContext.TicketInteractionMessages
            .Where(m => m.TicketInteractionId == ticketInteractionId)
            .OrderBy(m => m.Sequence)
            .ToListAsync(cancellationToken);

    /// <summary>One query for every interaction's transcript — never one per interaction (the same no-N+1 discipline the customer-history read follows).</summary>
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<TicketInteractionMessage>>> ListMessagesForInteractionsAsync(
        IReadOnlyCollection<long> ticketInteractionIds, CancellationToken cancellationToken = default)
    {
        if (ticketInteractionIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyList<TicketInteractionMessage>>();
        }

        var messages = await dbContext.TicketInteractionMessages
            .Where(m => ticketInteractionIds.Contains(m.TicketInteractionId))
            .OrderBy(m => m.TicketInteractionId)
            .ThenBy(m => m.Sequence)
            .ToListAsync(cancellationToken);

        return messages
            .GroupBy(m => m.TicketInteractionId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TicketInteractionMessage>)g.ToList());
    }

    public Task<int> CountMessagesAsync(long ticketInteractionId, CancellationToken cancellationToken = default) =>
        dbContext.TicketInteractionMessages.CountAsync(m => m.TicketInteractionId == ticketInteractionId, cancellationToken);

    public async Task AddMessageAsync(TicketInteractionMessage message, CancellationToken cancellationToken = default) =>
        await dbContext.TicketInteractionMessages.AddAsync(message, cancellationToken);
}

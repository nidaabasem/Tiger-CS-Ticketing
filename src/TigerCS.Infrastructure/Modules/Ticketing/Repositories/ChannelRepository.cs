using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class ChannelRepository(TigerCsDbContext dbContext) : IChannelRepository
{
    public Task<Channel?> GetByIdAsync(byte channelId, CancellationToken cancellationToken = default) =>
        dbContext.Channels.FirstOrDefaultAsync(c => c.ChannelId == channelId, cancellationToken);

    public Task<Channel?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        // Codes are unique case-insensitively (the SQL Server default
        // collation already compares that way; ToLower keeps the InMemory
        // test provider honest about the same rule).
        var normalized = code.Trim().ToLowerInvariant();
        return dbContext.Channels.FirstOrDefaultAsync(c => c.Code.ToLower() == normalized, cancellationToken);
    }

    public async Task<IReadOnlyList<Channel>> ListAsync(bool activeOnly, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Channels.AsQueryable();
        if (activeOnly)
        {
            query = query.Where(c => c.IsActive);
        }

        return await query
            .OrderBy(c => c.DisplayOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Channel channel, CancellationToken cancellationToken = default) =>
        await dbContext.Channels.AddAsync(channel, cancellationToken);

    public Task<bool> CodeExistsAsync(string code, byte? excludeChannelId, CancellationToken cancellationToken = default)
    {
        var normalized = code.Trim().ToLowerInvariant();
        return dbContext.Channels.AnyAsync(
            c => c.Code.ToLower() == normalized && (excludeChannelId == null || c.ChannelId != excludeChannelId),
            cancellationToken);
    }

    public async Task<int> CountReferencesAsync(byte channelId, CancellationToken cancellationToken = default) =>
        await dbContext.IntakeRecords.CountAsync(i => i.ChannelId == channelId, cancellationToken)
        + await dbContext.TicketInteractions.CountAsync(i => i.ChannelId == channelId, cancellationToken);
}

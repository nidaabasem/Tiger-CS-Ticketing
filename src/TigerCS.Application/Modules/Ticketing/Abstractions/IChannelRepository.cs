using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>The configurable <see cref="Channel"/> catalogue — Admin → Configuration → Channels on the write side, the Create Ticket channel picker on the read side.</summary>
public interface IChannelRepository
{
    Task<Channel?> GetByIdAsync(byte channelId, CancellationToken cancellationToken = default);

    /// <summary>Case-insensitive lookup by the stable <see cref="Channel.Code"/> — the API-contract identifier.</summary>
    Task<Channel?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Ordered by <see cref="Channel.DisplayOrder"/> then <see cref="Channel.Name"/>. <paramref name="activeOnly"/> true is what a new-ticket picker reads.</summary>
    Task<IReadOnlyList<Channel>> ListAsync(bool activeOnly, CancellationToken cancellationToken = default);

    Task AddAsync(Channel channel, CancellationToken cancellationToken = default);

    /// <summary>Case-insensitive code uniqueness check, optionally excluding the channel being edited.</summary>
    Task<bool> CodeExistsAsync(string code, byte? excludeChannelId, CancellationToken cancellationToken = default);

    /// <summary>How many intake records and ticket interactions reference the channel — the history that makes it undeletable.</summary>
    Task<int> CountReferencesAsync(byte channelId, CancellationToken cancellationToken = default);
}

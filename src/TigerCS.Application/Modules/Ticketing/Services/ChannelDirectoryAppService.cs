using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The channel directory the Create Ticket wizard reads its channel list
/// from — the configured <see cref="Channel"/> rows, never a hard-coded
/// list. <c>activeOnly</c> true (a new-ticket picker) excludes retired
/// channels; false exists only to put a NAME on a channel a historical
/// record already references.
/// </summary>
public sealed class ChannelDirectoryAppService(IChannelRepository channelRepository)
{
    public async Task<IReadOnlyList<ChannelDto>> ListAsync(bool activeOnly, CancellationToken cancellationToken = default)
    {
        var channels = await channelRepository.ListAsync(activeOnly, cancellationToken);
        return channels.Select(ToDto).ToList();
    }

    public static ChannelDto ToDto(Channel channel) => new(
        channel.ChannelId, channel.Name, channel.Code, channel.RequiresPhone, channel.IsGenesysEnabled, channel.IsActive, channel.DisplayOrder);
}

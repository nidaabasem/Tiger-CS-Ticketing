namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>One channel of the configurable channel catalogue — what the Create Ticket channel picker reads.</summary>
/// <param name="ChannelId">The channel's id — the value <c>CreateIntakeRecordRequestDto.ChannelId</c> binds to.</param>
/// <param name="Name">Human-readable channel name.</param>
/// <param name="Code">Stable unique code (also accepted by <c>CreateIntakeRecordRequestDto.ChannelId</c>).</param>
/// <param name="RequiresPhone">Whether Create Ticket Step 1 must capture a phone number for this channel.</param>
/// <param name="IsGenesysEnabled">Whether the channel originates/interacts through Genesys (configuration metadata).</param>
/// <param name="IsActive">Whether the channel may be selected for a new ticket.</param>
/// <param name="DisplayOrder">Position in the Create Ticket channel list.</param>
public sealed record ChannelDto(
    byte ChannelId,
    string Name,
    string Code,
    bool RequiresPhone,
    bool IsGenesysEnabled,
    bool IsActive,
    int DisplayOrder);

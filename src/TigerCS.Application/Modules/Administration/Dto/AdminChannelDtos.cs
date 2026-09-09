namespace TigerCS.Application.Modules.Administration.Dto;

/// <summary>A channel as the Channels administration list/detail shows it, with the history count that makes it undeletable.</summary>
public sealed record AdminChannelDto(
    byte ChannelId,
    string Name,
    string Code,
    bool RequiresPhone,
    bool IsGenesysEnabled,
    bool IsActive,
    int DisplayOrder,
    int ReferenceCount);

/// <summary>Add/Edit Channel form. <paramref name="IsActive"/> is honoured on create and edit alike; the activation endpoint exists for the one-click Activate/Deactivate actions.</summary>
public sealed record SaveChannelRequestDto(
    string Name,
    string Code,
    bool RequiresPhone,
    bool IsGenesysEnabled,
    int DisplayOrder,
    bool IsActive = true);

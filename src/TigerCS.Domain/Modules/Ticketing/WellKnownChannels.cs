namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// The ids of the channels seeded with the system — the members of the
/// former <c>Channel</c> enum, with their exact original values, so every
/// existing <c>IntakeRecords.ChannelId</c> / <c>TicketInteractions.ChannelId</c>
/// value keeps pointing at the same channel after the move to the
/// configurable <see cref="Channel"/> table. Any channel an administrator
/// adds later has an identity-generated id above these; nothing in the
/// application branches on these constants to decide behaviour — the
/// channel's own configuration (<see cref="Channel.RequiresPhone"/>,
/// <see cref="Channel.IsGenesysEnabled"/>) does.
/// </summary>
public static class WellKnownChannels
{
    public const byte Phone = 1;
    public const byte AppOrWebsite = 2;
    public const byte WhatsAppOrLiveChat = 3;
    public const byte SocialMediaDirectMessage = 4;
    public const byte FaceToFaceKiosk = 5;
}

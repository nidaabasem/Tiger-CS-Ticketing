namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// The ids of the channels seeded with the system, so every existing
/// <c>IntakeRecords.ChannelId</c> / <c>TicketInteractions.ChannelId</c>
/// value keeps pointing at the same channel. Ids 1..5 are the members of
/// the former <c>Channel</c> enum, kept at their exact original values for
/// historical compatibility; ids 2 and 3 are now retained only as inactive
/// legacy rows (<c>AppOrWebsite</c>, <c>WhatsAppOrLiveChat</c>) that
/// historical records still resolve. Ids 6..11 are the additional approved
/// production channels. Any channel an administrator adds later has an
/// identity-generated id above these; nothing in the application branches
/// on these constants to decide behaviour — the channel's own configuration
/// (<see cref="Channel.RequiresPhone"/>, <see cref="Channel.IsGenesysEnabled"/>)
/// does.
/// </summary>
public static class WellKnownChannels
{
    public const byte Phone = 1;

    /// <summary>Legacy, inactive — retained so historical records resolve.</summary>
    public const byte AppOrWebsite = 2;

    /// <summary>Legacy, inactive — retained so historical records resolve.</summary>
    public const byte WhatsAppOrLiveChat = 3;

    public const byte SocialMediaDirectMessage = 4;

    /// <summary>Id 5 — now "Walk in / Kiosk" (formerly "Face to Face / Kiosk").</summary>
    public const byte FaceToFaceKiosk = 5;

    public const byte WhatsApp = 6;
    public const byte LiveChat = 7;
    public const byte Website = 8;
    public const byte MobileApp = 9;
    public const byte Instagram = 10;
    public const byte Facebook = 11;
}

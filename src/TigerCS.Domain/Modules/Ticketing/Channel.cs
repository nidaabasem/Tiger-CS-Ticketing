namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// FR-CH-02 — the configurable catalogue of channels a customer interaction
/// can arrive on (Phone, WhatsApp, Live Chat, Website, Walk in / Kiosk,
/// Mobile App, Instagram, Facebook, …). Was a fixed <c>enum</c>; is now a
/// <c>Channels</c> table administered under Admin → Configuration → Channels,
/// so a new channel is a row, not a release. The original enum members keep
/// their exact ids (<see cref="WellKnownChannels"/>) — including the retired
/// legacy rows (ids 2 and 3) — so every existing IntakeRecord and
/// TicketInteraction row still references the same channel.
///
/// <para>
/// <b>Never hard-deleted.</b> IntakeRecords and TicketInteractions reference
/// a channel as the originating channel of the interaction/ticket; those
/// references are history and must keep displaying the original channel
/// name even after it is retired. <see cref="IsActive"/> = false is the only
/// retirement path — an inactive channel disappears from the Create Ticket
/// channel list and nowhere else.
/// </para>
///
/// <para>
/// <b>Configuration, not hard-coded rules.</b> <see cref="RequiresPhone"/>
/// drives whether Create Ticket Step 1 demands a phone number (a Phone call
/// has one; a walk-in at a kiosk may not), and <see cref="IsGenesysEnabled"/>
/// records which channels originate/interact through Genesys so later
/// routing/integration work reads configuration instead of comparing channel
/// names. Neither flag is consulted through a channel-name comparison
/// anywhere in the codebase.
/// </para>
/// </summary>
public class Channel
{
    public const int NameMaxLength = 100;
    public const int CodeMaxLength = 50;

    public byte ChannelId { get; private set; }

    /// <summary>Human-readable channel name shown in pickers and on ticket history.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Stable, unique code — the API contract identifier (the original enum names are the seeded codes).</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>Whether Create Ticket Step 1 must capture a phone number for an interaction on this channel.</summary>
    public bool RequiresPhone { get; private set; }

    /// <summary>Whether interactions on this channel originate/interact through Genesys — configuration metadata for later routing, never a routing rule itself.</summary>
    public bool IsGenesysEnabled { get; private set; }

    /// <summary>Whether users may select this channel for a NEW ticket. Historical references are unaffected.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Position in the Create Ticket channel list (ascending), ties broken by <see cref="Name"/>.</summary>
    public int DisplayOrder { get; private set; }

    private Channel() { }

    public Channel(
        string name, string code, bool requiresPhone, bool isGenesysEnabled, int displayOrder = 0, bool isActive = true)
    {
        Name = ValidateName(name);
        Code = ValidateCode(code);
        RequiresPhone = requiresPhone;
        IsGenesysEnabled = isGenesysEnabled;
        DisplayOrder = displayOrder;
        IsActive = isActive;
    }

    /// <summary>
    /// The seeded reference rows, constructed with their fixed ids so the
    /// original enum values keep resolving to the same channels. Only for
    /// reference-data seeding (<c>ChannelReferenceData</c>) — new channels
    /// added through administration are identity-generated.
    /// </summary>
    public static Channel Seeded(
        byte channelId, string name, string code, bool requiresPhone, bool isGenesysEnabled, int displayOrder, bool isActive = true)
    {
        if (channelId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelId), "A seeded channel needs a fixed, non-zero id.");
        }

        return new Channel(name, code, requiresPhone, isGenesysEnabled, displayOrder, isActive) { ChannelId = channelId };
    }

    /// <summary>Administration edit. Historical records keep referencing the same ChannelId, so they display the new name — the identity is the id, never the text.</summary>
    public void Update(string name, string code, bool requiresPhone, bool isGenesysEnabled, int displayOrder)
    {
        Name = ValidateName(name);
        Code = ValidateCode(code);
        RequiresPhone = requiresPhone;
        IsGenesysEnabled = isGenesysEnabled;
        DisplayOrder = displayOrder;
    }

    public void Activate() => IsActive = true;

    public void Deactivate() => IsActive = false;

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        var trimmed = name.Trim();
        if (trimmed.Length > NameMaxLength)
        {
            throw new ArgumentException($"Name must be at most {NameMaxLength} characters.", nameof(name));
        }

        return trimmed;
    }

    private static string ValidateCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Code is required.", nameof(code));
        }

        var trimmed = code.Trim();
        if (trimmed.Length > CodeMaxLength)
        {
            throw new ArgumentException($"Code must be at most {CodeMaxLength} characters.", nameof(code));
        }

        return trimmed;
    }
}

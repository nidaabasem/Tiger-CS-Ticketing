using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Seed;

/// <summary>
/// The channel reference rows — the approved production channel list, keyed
/// by their fixed ids (<see cref="WellKnownChannels"/>) with stable codes,
/// so every pre-existing <c>IntakeRecords.ChannelId</c> /
/// <c>TicketInteractions.ChannelId</c> value (and every API client still
/// sending a channel code) resolves to the same channel. Ids 2 and 3 are
/// retained as inactive legacy rows so historical records keep resolving
/// their names; the active channels carry the approved display order. The
/// <c>AddChannels</c> migration inserts the same rows for a migrated
/// database; this seed exists for databases created without migrations
/// (the InMemory-backed integration tests) and is idempotent per row.
///
/// <para>
/// This seed never overwrites existing rows — administrator edits under
/// Admin → Configuration → Channels are preserved; only missing ids are
/// inserted.
/// </para>
/// </summary>
public static class ChannelReferenceData
{
    public static IReadOnlyList<Channel> Channels() =>
    [
        Channel.Seeded(WellKnownChannels.Phone, "Phone", "PHONE", requiresPhone: true, isGenesysEnabled: true, displayOrder: 1, isActive: true),
        Channel.Seeded(WellKnownChannels.AppOrWebsite, "App / Website (Legacy)", "LEGACY_APP_OR_WEBSITE", requiresPhone: true, isGenesysEnabled: false, displayOrder: 101, isActive: false),
        Channel.Seeded(WellKnownChannels.WhatsAppOrLiveChat, "WhatsApp / Live Chat (Legacy)", "LEGACY_WHATSAPP_OR_LIVE_CHAT", requiresPhone: true, isGenesysEnabled: true, displayOrder: 102, isActive: false),
        Channel.Seeded(WellKnownChannels.SocialMediaDirectMessage, "Social Media Direct Message", "SOCIAL_DM", requiresPhone: true, isGenesysEnabled: true, displayOrder: 4, isActive: true),
        Channel.Seeded(WellKnownChannels.FaceToFaceKiosk, "Walk in / Kiosk", "WALK_IN_KIOSK", requiresPhone: false, isGenesysEnabled: false, displayOrder: 6, isActive: true),
        Channel.Seeded(WellKnownChannels.WhatsApp, "WhatsApp", "WHATSAPP", requiresPhone: true, isGenesysEnabled: true, displayOrder: 2, isActive: true),
        Channel.Seeded(WellKnownChannels.LiveChat, "Live Chat", "LIVE_CHAT", requiresPhone: true, isGenesysEnabled: true, displayOrder: 3, isActive: true),
        Channel.Seeded(WellKnownChannels.Website, "Website", "WEBSITE", requiresPhone: true, isGenesysEnabled: false, displayOrder: 5, isActive: true),
        Channel.Seeded(WellKnownChannels.MobileApp, "Mobile App (Customer Portal)", "MOBILE_APP", requiresPhone: true, isGenesysEnabled: false, displayOrder: 7, isActive: true),
        Channel.Seeded(WellKnownChannels.Instagram, "Instagram", "INSTAGRAM", requiresPhone: true, isGenesysEnabled: true, displayOrder: 8, isActive: true),
        Channel.Seeded(WellKnownChannels.Facebook, "Facebook", "FACEBOOK", requiresPhone: true, isGenesysEnabled: true, displayOrder: 9, isActive: true)
    ];

    /// <summary>
    /// Inserts any seeded channel whose id is missing. Existing rows —
    /// including administrator edits to a seeded channel — are never
    /// touched. Safe to call on every startup.
    /// </summary>
    public static async Task SeedAsync(TigerCsDbContext dbContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var existingIds = await dbContext.Channels.Select(c => c.ChannelId).ToListAsync(cancellationToken);
        var missing = Channels().Where(c => !existingIds.Contains(c.ChannelId)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        if (dbContext.Database.IsSqlServer())
        {
            // The seeded rows carry their fixed ids into an identity column,
            // which SQL Server only accepts under IDENTITY_INSERT — on the
            // same connection and transaction as the insert.
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT [Channels] ON", cancellationToken);
            dbContext.Channels.AddRange(missing);
            await dbContext.SaveChangesAsync(cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT [Channels] OFF", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        dbContext.Channels.AddRange(missing);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

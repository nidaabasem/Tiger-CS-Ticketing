using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Seed;

/// <summary>
/// The channel reference rows — the five members of the former
/// <c>Channel</c> enum, with their exact original ids
/// (<see cref="WellKnownChannels"/>) and the enum names as their stable
/// codes, so every pre-existing <c>IntakeRecords.ChannelId</c> /
/// <c>TicketInteractions.ChannelId</c> value (and every API client still
/// sending "Phone") resolves to the same channel. The
/// <c>AddChannels</c> migration inserts the same rows for a migrated
/// database; this seed exists for databases created without migrations
/// (the InMemory-backed integration tests) and is idempotent per row.
///
/// <para>
/// Only the five rows are seeded, deliberately: the request's example list
/// (WhatsApp and Live Chat as separate channels, etc.) is not recreated
/// because seed data already existed as the enum — an administrator adds
/// or renames channels under Admin → Configuration → Channels, and this
/// seed never overwrites those edits.
/// </para>
/// </summary>
public static class ChannelReferenceData
{
    public static IReadOnlyList<Channel> Channels() =>
    [
        Channel.Seeded(WellKnownChannels.Phone, "Phone", "Phone", requiresPhone: true, isGenesysEnabled: true, displayOrder: 1),
        Channel.Seeded(WellKnownChannels.AppOrWebsite, "App / Website", "AppOrWebsite", requiresPhone: true, isGenesysEnabled: false, displayOrder: 2),
        Channel.Seeded(WellKnownChannels.WhatsAppOrLiveChat, "WhatsApp / Live Chat", "WhatsAppOrLiveChat", requiresPhone: true, isGenesysEnabled: true, displayOrder: 3),
        Channel.Seeded(WellKnownChannels.SocialMediaDirectMessage, "Social Media Direct Message", "SocialMediaDirectMessage", requiresPhone: false, isGenesysEnabled: true, displayOrder: 4),
        Channel.Seeded(WellKnownChannels.FaceToFaceKiosk, "Face to Face / Kiosk", "FaceToFaceKiosk", requiresPhone: false, isGenesysEnabled: false, displayOrder: 5)
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

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Collections;

/// <summary>
/// Reminder jobs store what TigerCS quoted and did — never a balance, which
/// is always read from the financial source. Amounts are decimal(19,4) so a
/// source figure is stored exactly as reported.
/// </summary>
public class CollectionsReminderConfiguration : IEntityTypeConfiguration<CollectionsReminder>
{
    public void Configure(EntityTypeBuilder<CollectionsReminder> builder)
    {
        builder.ToTable("CollectionsReminders");

        builder.HasKey(r => r.CollectionsReminderId);
        builder.Property(r => r.CollectionsReminderId).ValueGeneratedOnAdd();
        builder.Ignore(r => r.PublicId);

        builder.Property(r => r.AccountId).HasMaxLength(CollectionsReminder.AccountIdMaxLength).IsRequired();
        builder.Property(r => r.Type).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(r => r.CycleKey).HasMaxLength(CollectionsReminder.CycleKeyMaxLength).IsRequired();
        builder.Property(r => r.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(r => r.Amount).HasPrecision(19, 4);
        builder.Property(r => r.AmountBasis).HasMaxLength(CollectionsReminder.AmountBasisMaxLength).IsRequired();
        builder.Property(r => r.InstalmentIds).HasMaxLength(CollectionsReminder.InstalmentIdsMaxLength).IsRequired();
        builder.Property(r => r.Language).HasMaxLength(2).IsUnicode(false).IsRequired();
        builder.Property(r => r.Trigger).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.IdempotencyKey).HasMaxLength(CollectionsReminder.IdempotencyKeyMaxLength);
        builder.Property(r => r.RequestHash).HasMaxLength(64).IsFixedLength().IsUnicode(false);

        builder.HasIndex(r => r.IdempotencyKey)
            .IsUnique()
            .HasFilter("[IdempotencyKey] IS NOT NULL")
            .HasDatabaseName("UX_CollectionsReminders_IdempotencyKey");

        builder.HasIndex(r => new { r.CrmCustomerId, r.QueuedAtUtc })
            .HasDatabaseName("IX_CollectionsReminders_Customer_Queued");

        builder.HasMany(r => r.Channels).WithOne(c => c.Reminder).HasForeignKey(c => c.CollectionsReminderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(r => r.Events).WithOne(e => e.Reminder).HasForeignKey(e => e.CollectionsReminderId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(r => r.Channels).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(r => r.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class CollectionsReminderChannelConfiguration : IEntityTypeConfiguration<CollectionsReminderChannel>
{
    public void Configure(EntityTypeBuilder<CollectionsReminderChannel> builder)
    {
        builder.ToTable("CollectionsReminderChannels");

        builder.HasKey(c => c.CollectionsReminderChannelId);
        builder.Property(c => c.CollectionsReminderChannelId).ValueGeneratedOnAdd();
        builder.Ignore(c => c.CanRetry);

        builder.Property(c => c.Channel).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(c => c.DeduplicationKey).HasMaxLength(CollectionsReminderChannel.DeduplicationKeyMaxLength).IsRequired();
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(c => c.StatusReason).HasMaxLength(CollectionsReminder.ReasonMaxLength);
        builder.Property(c => c.ProviderMessageId).HasMaxLength(CollectionsReminderChannel.ProviderMessageIdMaxLength);
        builder.Property(c => c.DispatchAmount).HasPrecision(19, 4);

        // Duplicate prevention per account / type / cycle / channel — the
        // database guarantee behind the service's read-before-write, and what
        // makes concurrent schedulers and duplicate requests unable to send a
        // channel twice in a cycle.
        builder.HasIndex(c => c.DeduplicationKey)
            .IsUnique()
            .HasDatabaseName("UX_CollectionsReminderChannels_DeduplicationKey");
    }
}

public class CollectionsReminderEventConfiguration : IEntityTypeConfiguration<CollectionsReminderEvent>
{
    public void Configure(EntityTypeBuilder<CollectionsReminderEvent> builder)
    {
        builder.ToTable("CollectionsReminderEvents");

        builder.HasKey(e => e.CollectionsReminderEventId);
        builder.Property(e => e.CollectionsReminderEventId).ValueGeneratedOnAdd();
        builder.Ignore(e => e.FollowUpRequired);

        builder.Property(e => e.ExternalEventId).HasMaxLength(CollectionsReminderEvent.ExternalEventIdMaxLength).IsRequired();
        builder.Property(e => e.IdempotencyKey).HasMaxLength(CollectionsReminder.IdempotencyKeyMaxLength);
        builder.Property(e => e.RequestHash).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        builder.Property(e => e.Channel).HasConversion<string>().HasMaxLength(16);
        builder.Property(e => e.DeliveryStatus).HasConversion<string>().HasMaxLength(16);
        builder.Property(e => e.ProviderMessageId).HasMaxLength(CollectionsReminderChannel.ProviderMessageIdMaxLength);
        builder.Property(e => e.ConversationId).HasMaxLength(CollectionsReminderEvent.ConversationIdMaxLength);
        builder.Property(e => e.Detail).HasMaxLength(CollectionsReminderEvent.DetailMaxLength);
        builder.Property(e => e.CustomerIntent).HasConversion<string>().HasMaxLength(24);
        builder.Property(e => e.CustomerPhone).HasMaxLength(CollectionsReminderEvent.PhoneMaxLength);
        builder.Property(e => e.TicketResult).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(e => e.TicketNumber).HasMaxLength(CollectionsReminderEvent.TicketNumberMaxLength);
        builder.Property(e => e.TicketLastError).HasMaxLength(CollectionsReminder.ReasonMaxLength);

        // Callback idempotency: one row per (reminder, caller's event id).
        builder.HasIndex(e => new { e.CollectionsReminderId, e.ExternalEventId })
            .IsUnique()
            .HasDatabaseName("UX_CollectionsReminderEvents_Reminder_EventId");

        builder.HasIndex(e => e.TicketId).HasDatabaseName("IX_CollectionsReminderEvents_TicketId");
    }
}

public sealed class CollectionsReminderRepository(TigerCsDbContext dbContext) : ICollectionsReminderRepository
{
    private IQueryable<CollectionsReminder> Jobs =>
        dbContext.CollectionsReminders.Include(r => r.Channels).Include(r => r.Events).AsSplitQuery();

    public Task<CollectionsReminder?> GetByIdAsync(long reminderId, CancellationToken cancellationToken = default) =>
        Jobs.FirstOrDefaultAsync(r => r.CollectionsReminderId == reminderId, cancellationToken);

    public Task<CollectionsReminder?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default) =>
        Jobs.FirstOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task<IReadOnlyList<CollectionsReminderChannel>> GetChannelsByDeduplicationKeysAsync(
        IReadOnlyCollection<string> deduplicationKeys, CancellationToken cancellationToken = default)
    {
        var found = new List<CollectionsReminderChannel>();
        // Chunked to stay well under SQL Server's parameter limit.
        foreach (var chunk in deduplicationKeys.Distinct().Chunk(500))
        {
            var keys = chunk.ToList();
            found.AddRange(await dbContext.CollectionsReminderChannels
                .Where(c => keys.Contains(c.DeduplicationKey))
                .ToListAsync(cancellationToken));
        }

        return found;
    }

    public async Task AddAsync(CollectionsReminder reminder, CancellationToken cancellationToken = default) =>
        await dbContext.CollectionsReminders.AddAsync(reminder, cancellationToken);

    public async Task<(IReadOnlyList<CollectionsReminder> Items, bool HasMore)> ListForCustomerAsync(
        long crmCustomerId, string? accountId, int offset, int take, CancellationToken cancellationToken = default)
    {
        var query = dbContext.CollectionsReminders.Where(r => r.CrmCustomerId == crmCustomerId);
        if (accountId is not null)
        {
            query = query.Where(r => r.AccountId == accountId);
        }

        var items = await query
            .OrderByDescending(r => r.QueuedAtUtc).ThenByDescending(r => r.CollectionsReminderId)
            .Skip(offset).Take(take + 1)
            .Include(r => r.Channels).Include(r => r.Events)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        return (items.Take(take).ToList(), items.Count > take);
    }
}

public sealed class CollectionsUnitOfWork(TigerCsDbContext dbContext) : ICollectionsUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            throw new DuplicateWriteException(ex);
        }
    }

    public void DiscardPendingChanges()
    {
        foreach (var entry in dbContext.ChangeTracker.Entries().Where(e => e.State != EntityState.Unchanged).ToList())
        {
            if (entry.State == EntityState.Added)
            {
                entry.State = EntityState.Detached;
            }
            else
            {
                // Back to what the database holds, so a re-read sees the winner.
                entry.Reload();
            }
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SqlException { Number: 2601 or 2627 })
            {
                return true;
            }
        }

        return false;
    }
}

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Collections;

/// <summary>
/// Reminders store what TigerCS decided and sent — never a balance. Amounts
/// are decimal(19,4) so a source figure is stored exactly as reported.
/// </summary>
public class CollectionsReminderConfiguration : IEntityTypeConfiguration<CollectionsReminder>
{
    public void Configure(EntityTypeBuilder<CollectionsReminder> builder)
    {
        builder.ToTable("CollectionsReminders");

        builder.HasKey(r => r.CollectionsReminderId);
        builder.Property(r => r.CollectionsReminderId).ValueGeneratedOnAdd();

        builder.Property(r => r.CrmCustomerId).HasMaxLength(CollectionsReminder.IdentifierMaxLength).IsRequired();
        builder.Property(r => r.AccountId).HasMaxLength(CollectionsReminder.IdentifierMaxLength).IsRequired();
        builder.Property(r => r.CrmUnitId).HasMaxLength(CollectionsReminder.IdentifierMaxLength);
        builder.Property(r => r.Type).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(r => r.Channel).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.CycleKey).HasMaxLength(CollectionsReminder.CycleKeyMaxLength).IsRequired();
        builder.Property(r => r.DeduplicationKey).HasMaxLength(CollectionsReminder.DeduplicationKeyMaxLength).IsRequired();
        builder.Property(r => r.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(r => r.Amount).HasPrecision(19, 4);
        builder.Property(r => r.DispatchAmount).HasPrecision(19, 4);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.StatusReason).HasMaxLength(CollectionsReminder.ReasonMaxLength);
        builder.Property(r => r.ProviderReference).HasMaxLength(CollectionsReminder.ProviderReferenceMaxLength);
        builder.Property(r => r.Trigger).HasConversion<string>().HasMaxLength(16).IsRequired();

        // Duplicate prevention per account / type / cycle / channel — the
        // database guarantee behind the service's read-before-write.
        builder.HasIndex(r => r.DeduplicationKey)
            .IsUnique()
            .HasDatabaseName("UX_CollectionsReminders_DeduplicationKey");

        builder.HasIndex(r => new { r.CrmCustomerId, r.CreatedAtUtc })
            .HasDatabaseName("IX_CollectionsReminders_Customer_Created");

        builder.HasMany(r => r.Events)
            .WithOne(e => e.Reminder)
            .HasForeignKey(e => e.CollectionsReminderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Navigation(r => r.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class CollectionsReminderEventConfiguration : IEntityTypeConfiguration<CollectionsReminderEvent>
{
    public void Configure(EntityTypeBuilder<CollectionsReminderEvent> builder)
    {
        builder.ToTable("CollectionsReminderEvents");

        builder.HasKey(e => e.CollectionsReminderEventId);
        builder.Property(e => e.CollectionsReminderEventId).ValueGeneratedOnAdd();

        builder.Property(e => e.ExternalEventId).HasMaxLength(CollectionsReminderEvent.ExternalEventIdMaxLength).IsRequired();
        builder.Property(e => e.EventType).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(e => e.Detail).HasMaxLength(CollectionsReminderEvent.NoteMaxLength);
        builder.Property(e => e.ResponseKind).HasConversion<string>().HasMaxLength(24);
        builder.Property(e => e.ConversationId).HasMaxLength(CollectionsReminderEvent.ConversationIdMaxLength);
        builder.Property(e => e.CustomerPhone).HasMaxLength(CollectionsReminderEvent.PhoneMaxLength);
        builder.Property(e => e.PromisedAmount).HasPrecision(19, 4);
        builder.Property(e => e.TicketStatus).HasConversion<string>().HasMaxLength(16);
        builder.Property(e => e.TicketNumber).HasMaxLength(CollectionsReminderEvent.TicketNumberMaxLength);
        builder.Property(e => e.TicketLastError).HasMaxLength(CollectionsReminder.ReasonMaxLength);

        // Callback idempotency: one row per (reminder, caller's event id).
        builder.HasIndex(e => new { e.CollectionsReminderId, e.ExternalEventId })
            .IsUnique()
            .HasDatabaseName("UX_CollectionsReminderEvents_Reminder_EventId");

        // The linked ticket, for "which reminder produced this ticket". No FK:
        // the ticket is Ticketing's, and a reminder event must never block it.
        builder.HasIndex(e => e.TicketId)
            .HasDatabaseName("IX_CollectionsReminderEvents_TicketId");
    }
}

public sealed class CollectionsReminderRepository(TigerCsDbContext dbContext) : ICollectionsReminderRepository
{
    public Task<CollectionsReminder?> GetByIdAsync(long reminderId, CancellationToken cancellationToken = default) =>
        dbContext.CollectionsReminders.Include(r => r.Events)
            .FirstOrDefaultAsync(r => r.CollectionsReminderId == reminderId, cancellationToken);

    public Task<CollectionsReminder?> GetByDeduplicationKeyAsync(string deduplicationKey, CancellationToken cancellationToken = default) =>
        dbContext.CollectionsReminders.Include(r => r.Events)
            .FirstOrDefaultAsync(r => r.DeduplicationKey == deduplicationKey, cancellationToken);

    public async Task<IReadOnlySet<string>> GetExistingDeduplicationKeysAsync(
        IReadOnlyCollection<string> deduplicationKeys, CancellationToken cancellationToken = default)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        // Chunked to stay well under SQL Server's parameter limit.
        foreach (var chunk in deduplicationKeys.Distinct().Chunk(500))
        {
            var keys = chunk.ToList();
            found.UnionWith(await dbContext.CollectionsReminders
                .Where(r => keys.Contains(r.DeduplicationKey))
                .Select(r => r.DeduplicationKey)
                .ToListAsync(cancellationToken));
        }

        return found;
    }

    public async Task AddAsync(CollectionsReminder reminder, CancellationToken cancellationToken = default) =>
        await dbContext.CollectionsReminders.AddAsync(reminder, cancellationToken);

    public async Task<(IReadOnlyList<CollectionsReminder> Items, int TotalCount)> ListForCustomerAsync(
        string crmCustomerId, string? accountId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = dbContext.CollectionsReminders.Where(r => r.CrmCustomerId == crmCustomerId);
        if (accountId is not null)
        {
            query = query.Where(r => r.AccountId == accountId);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.CollectionsReminderId)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Include(r => r.Events)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public Task<CollectionsReminderEvent?> GetEventAsync(long reminderEventId, CancellationToken cancellationToken = default) =>
        dbContext.CollectionsReminderEvents.Include(e => e.Reminder)
            .FirstOrDefaultAsync(e => e.CollectionsReminderEventId == reminderEventId, cancellationToken);
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

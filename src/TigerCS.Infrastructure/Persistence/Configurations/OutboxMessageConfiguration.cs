using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Infrastructure;

namespace TigerCS.Infrastructure.Persistence.Configurations;

/// <summary>
/// MVP-Data-Dictionary.md §2.23 (the <c>OutboxMessages</c> half) / MVP-ERD.md
/// §2.23 / ADR-0013. Lives beside <see cref="IdempotencyRecordConfiguration"/>
/// in the Infrastructure-owned persistence folder rather than under a module
/// folder, because Module-Design.md assigns <c>OutboxMessage</c> to the
/// Infrastructure module, not to Notifications.
/// </summary>
public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");

        builder.HasKey(m => m.OutboxMessageId);

        // Client-generated, never database-generated: the writer needs the id
        // in hand to link a Notifications row to it, and a NEWID() default
        // would also fragment the clustered index on every insert.
        builder.Property(m => m.OutboxMessageId).ValueGeneratedNever();

        builder.Property(m => m.EventType).HasMaxLength(200).IsRequired();
        builder.Property(m => m.Payload).IsRequired();
        builder.Property(m => m.CorrelationId).IsRequired();
        builder.Property(m => m.IdempotencyRecordId).IsRequired();
        builder.Property(m => m.Status).HasConversion<byte>().IsRequired();
        builder.Property(m => m.LastError).HasMaxLength(2000);
        builder.Property(m => m.OccurredAtUtc).IsRequired();
        builder.Property(m => m.ProcessedAtUtc);

        // The concurrency token that makes a dispatch claim atomic — see
        // OutboxMessage.BeginAttempt's remarks. EF emits
        // "UPDATE ... WHERE OutboxMessageId = @id AND Attempts = @original",
        // so exactly one of two racing workers can claim a message. This adds
        // no column and changes no DDL; it changes only the WHERE clause EF
        // generates, which is why it can be applied to an approved column
        // without departing from the approved schema.
        builder.Property(m => m.Attempts).IsRequired().IsConcurrencyToken();

        // MVP-ERD.md §2.23: "OutboxMessages -> IdempotencyRecords, many:1,
        // Required, Restrict". Restrict because an idempotency record is the
        // proof a logical event was already handled — deleting one because
        // its message was cleaned up would silently re-open the door to a
        // duplicate.
        builder.HasOne(m => m.IdempotencyRecord)
            .WithMany()
            .HasForeignKey(m => m.IdempotencyRecordId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired();

        // The dispatcher's candidate query: pending messages, oldest first,
        // filtered on attempt count. Filtered to Pending because Processed
        // rows accumulate indefinitely (nothing deletes them) and must not
        // grow the index the hot path scans.
        builder.HasIndex(m => new { m.Status, m.OccurredAtUtc })
            .HasDatabaseName("IX_OutboxMessages_Status_OccurredAtUtc")
            .HasFilter("[Status] = 1");

        // Operational visibility for the dead-letter review Administration
        // owns (Module-Design.md) — and the backing index for the
        // dead-letter-count metric ADR-0020 alerts on.
        builder.HasIndex(m => m.Status)
            .HasDatabaseName("IX_OutboxMessages_DeadLettered")
            .HasFilter("[Status] = 3");

        builder.HasIndex(m => m.CorrelationId)
            .HasDatabaseName("IX_OutboxMessages_CorrelationId");
    }
}

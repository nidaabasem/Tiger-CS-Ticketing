using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Infrastructure;

namespace TigerCS.Infrastructure.Persistence.Configurations;

/// <summary>
/// MVP-Data-Dictionary.md §2.23 (the <c>IdempotencyRecords</c> half) /
/// ADR-0014.
///
/// <para>
/// <c>OutboxMessages</c> — §2.23's other half, and the FK it holds onto this
/// table — belongs to backlog S-05's Outbox dispatch loop and is not mapped
/// here. This increment builds only the idempotency half, which is what the
/// SLA breach check needs (SLA-Architecture.md §15).
/// </para>
/// </summary>
public class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("IdempotencyRecords");

        builder.HasKey(r => r.IdempotencyRecordId);
        builder.Property(r => r.IdempotencyRecordId).ValueGeneratedOnAdd();

        builder.Property(r => r.IdempotencyKey).HasMaxLength(300).IsRequired();
        builder.Property(r => r.Scope).HasMaxLength(50).IsRequired();
        builder.Property(r => r.FirstSeenAtUtc).IsRequired();
        builder.Property(r => r.LastSeenAtUtc).IsRequired();
        builder.Property(r => r.ResultReference).HasMaxLength(200);
        builder.Property(r => r.ExpiresAtUtc);

        // "IdempotencyKey ... Unique" (MVP-Data-Dictionary.md §2.23). This is
        // the constraint that makes the whole mechanism safe under genuine
        // concurrency rather than only under sequential retries: two
        // detection paths racing the same due event both pass the read-side
        // check, and exactly one of them survives the insert.
        builder.HasIndex(r => r.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("UX_IdempotencyRecords_IdempotencyKey");
    }
}

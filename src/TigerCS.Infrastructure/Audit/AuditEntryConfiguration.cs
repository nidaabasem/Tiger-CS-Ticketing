using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Audit;

namespace TigerCS.Infrastructure.Audit;

/// <summary>MVP-Data-Dictionary.md §2.22 / MVP-ERD.md §2.22.</summary>
public class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("AuditEntries");

        builder.HasKey(a => a.AuditEntryId);
        builder.Property(a => a.AuditEntryId).ValueGeneratedOnAdd();

        builder.Property(a => a.Action).HasMaxLength(100).IsRequired();
        builder.Property(a => a.EntityType).HasMaxLength(100).IsRequired();
        builder.Property(a => a.EntityId).HasMaxLength(100);
        builder.Property(a => a.BeforeValue);
        builder.Property(a => a.AfterValue);
        builder.Property(a => a.CorrelationId).IsRequired();
        builder.Property(a => a.OccurredAtUtc).IsRequired();

        // Deliberately no FK on EntityId (MVP-ERD.md §2.22) — soft reference only.
    }
}

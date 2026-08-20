using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.8 / MVP-ERD.md §2.8 (ADR-0007) — see TicketRequesterSnapshot's own remarks for why the relationship is configured 0/1:1, not the ERD's mandatory 1:1.</summary>
public class TicketRequesterSnapshotConfiguration : IEntityTypeConfiguration<TicketRequesterSnapshot>
{
    public void Configure(EntityTypeBuilder<TicketRequesterSnapshot> builder)
    {
        builder.ToTable("TicketRequesterSnapshots");

        builder.HasKey(s => s.TicketId);
        builder.Property(s => s.TicketId).ValueGeneratedNever();

        builder.Property(s => s.SnapshotUnitNumber).HasMaxLength(50).IsRequired();
        builder.Property(s => s.SnapshotPropertyName).HasMaxLength(200);
        builder.Property(s => s.SnapshotTowerName).HasMaxLength(200);
        builder.Property(s => s.SnapshotUnitType).HasMaxLength(50);
        builder.Property(s => s.SnapshotContactDisplayName).HasMaxLength(200);
        builder.Property(s => s.SnapshotContactChannel).HasMaxLength(200);
        builder.Property(s => s.CapturedAtUtc).IsRequired();

        builder.HasOne<Ticket>()
            .WithOne()
            .HasForeignKey<TicketRequesterSnapshot>(s => s.TicketId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

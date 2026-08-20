using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Infrastructure.Modules.CustomerVerification.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.7 / MVP-ERD.md §2.7.</summary>
public class UnitReferenceConfiguration : IEntityTypeConfiguration<UnitReference>
{
    public void Configure(EntityTypeBuilder<UnitReference> builder)
    {
        builder.ToTable("UnitReferences");

        builder.HasKey(u => u.UnitReferenceId);
        builder.Property(u => u.UnitReferenceId).ValueGeneratedOnAdd();

        builder.Property(u => u.CrmUnitId).HasMaxLength(64).IsRequired();
        builder.HasIndex(u => u.CrmUnitId).IsUnique();

        builder.Property(u => u.UnitNumber).HasMaxLength(50).IsRequired();
        builder.Property(u => u.PropertyName).HasMaxLength(200);
        builder.Property(u => u.TowerName).HasMaxLength(200);
        builder.Property(u => u.UnitType).HasMaxLength(50);
        builder.Property(u => u.LastSyncedAtUtc).IsRequired();

        builder.HasMany(u => u.Contacts)
            .WithOne()
            .HasForeignKey(c => c.UnitReferenceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

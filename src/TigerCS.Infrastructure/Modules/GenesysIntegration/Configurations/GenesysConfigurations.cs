using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.GenesysIntegration;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Infrastructure.Modules.GenesysIntegration.Configurations;

/// <summary>
/// The Genesys Queue → Department mapping table. The queue id is unique
/// (case-insensitive under the database's default collation), because a
/// queue routes to exactly one department at a time; a re-point is an edit,
/// never a second row.
/// </summary>
public class GenesysQueueMappingConfiguration : IEntityTypeConfiguration<GenesysQueueMapping>
{
    public void Configure(EntityTypeBuilder<GenesysQueueMapping> builder)
    {
        builder.ToTable("GenesysQueueMappings");

        builder.HasKey(m => m.GenesysQueueMappingId);
        builder.Property(m => m.GenesysQueueMappingId).ValueGeneratedOnAdd();

        builder.Property(m => m.QueueId).HasMaxLength(GenesysQueueMapping.QueueIdMaxLength).IsRequired();
        builder.Property(m => m.QueueName).HasMaxLength(GenesysQueueMapping.QueueNameMaxLength);
        builder.Property(m => m.DepartmentId).IsRequired();
        builder.Property(m => m.IsActive).IsRequired();
        builder.Property(m => m.CreatedAtUtc).IsRequired();
        builder.Property(m => m.UpdatedAtUtc).IsRequired();

        builder.HasIndex(m => m.QueueId)
            .IsUnique()
            .HasDatabaseName("UX_GenesysQueueMappings_QueueId");

        // Departments are deactivated, never deleted — Restrict, like every
        // other Departments-referencing FK in this schema.
        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(m => m.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// Per-department Genesys settings — one row per department (unique), naming
/// the category unclassified Genesys tickets are created under.
/// </summary>
public class GenesysDepartmentSettingsConfiguration : IEntityTypeConfiguration<GenesysDepartmentSettings>
{
    public void Configure(EntityTypeBuilder<GenesysDepartmentSettings> builder)
    {
        builder.ToTable("GenesysDepartmentSettings");

        builder.HasKey(s => s.GenesysDepartmentSettingsId);
        builder.Property(s => s.GenesysDepartmentSettingsId).ValueGeneratedOnAdd();

        builder.Property(s => s.DepartmentId).IsRequired();
        builder.Property(s => s.DefaultCategoryId).IsRequired();
        builder.Property(s => s.IsActive).IsRequired();
        builder.Property(s => s.CreatedAtUtc).IsRequired();
        builder.Property(s => s.UpdatedAtUtc).IsRequired();

        builder.HasIndex(s => s.DepartmentId)
            .IsUnique()
            .HasDatabaseName("UX_GenesysDepartmentSettings_DepartmentId");

        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(s => s.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Categories are deactivated, never deleted, for the same reason.
        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(s => s.DefaultCategoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
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

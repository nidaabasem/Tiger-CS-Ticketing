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

/// <summary>
/// Secure Screen Pop launches. The token hash is unique (it is the lookup
/// key); <c>RedeemedAtUtc</c> is a concurrency token, so the UPDATE that
/// consumes a launch carries <c>WHERE RedeemedAtUtc IS NULL</c> and a second,
/// concurrent redemption affects no row. No FK to AspNetUsers: a launch is a
/// short-lived credential record, and the user it names is re-validated on
/// redemption rather than trusted from this row.
/// </summary>
public class GenesysScreenPopLaunchConfiguration : IEntityTypeConfiguration<GenesysScreenPopLaunch>
{
    public void Configure(EntityTypeBuilder<GenesysScreenPopLaunch> builder)
    {
        builder.ToTable("GenesysScreenPopLaunches");

        builder.HasKey(l => l.GenesysScreenPopLaunchId);
        builder.Property(l => l.GenesysScreenPopLaunchId).ValueGeneratedOnAdd();

        builder.Property(l => l.TokenHash)
            .HasMaxLength(GenesysScreenPopLaunch.TokenHashLength)
            .IsFixedLength()
            .IsUnicode(false)
            .IsRequired();
        builder.Property(l => l.GenesysUserId).HasMaxLength(GenesysScreenPopLaunch.GenesysUserIdMaxLength).IsRequired();
        builder.Property(l => l.UserId).IsRequired();
        builder.Property(l => l.TargetPath).HasMaxLength(GenesysScreenPopLaunch.TargetPathMaxLength).IsRequired();
        builder.Property(l => l.ConversationId).HasMaxLength(GenesysScreenPopLaunch.ConversationIdMaxLength);
        builder.Property(l => l.IssuedByEmployeeId).IsRequired();
        builder.Property(l => l.IssuedAtUtc).IsRequired();
        builder.Property(l => l.ExpiresAtUtc).IsRequired();
        builder.Property(l => l.RedeemedAtUtc).IsConcurrencyToken();

        builder.HasIndex(l => l.TokenHash)
            .IsUnique()
            .HasDatabaseName("UX_GenesysScreenPopLaunches_TokenHash");
    }
}

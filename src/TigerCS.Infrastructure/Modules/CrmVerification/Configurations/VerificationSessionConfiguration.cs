using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.CrmVerification;

namespace TigerCS.Infrastructure.Modules.CrmVerification.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.24 / MVP-ERD.md §2.24.</summary>
public class VerificationSessionConfiguration : IEntityTypeConfiguration<VerificationSession>
{
    public void Configure(EntityTypeBuilder<VerificationSession> builder)
    {
        builder.ToTable("VerificationSessions");

        builder.HasKey(s => s.VerificationSessionId);
        builder.Property(s => s.VerificationSessionId).ValueGeneratedNever();

        builder.Property(s => s.AgentEmployeeId).IsRequired();
        builder.Property(s => s.UnitReferenceId).IsRequired();
        builder.Property(s => s.ContactReferenceId).IsRequired();

        builder.Property(s => s.SnapshotUnitNumber).HasMaxLength(50);
        builder.Property(s => s.SnapshotPropertyName).HasMaxLength(200);
        builder.Property(s => s.SnapshotTowerName).HasMaxLength(200);
        builder.Property(s => s.SnapshotUnitType).HasMaxLength(50);
        builder.Property(s => s.SnapshotContactDisplayName).HasMaxLength(200);
        builder.Property(s => s.SnapshotContactChannel).HasMaxLength(200);

        builder.Property(s => s.ConfirmedVerbally).IsRequired();
        builder.Property(s => s.Status).IsRequired();
        builder.Property(s => s.CreatedAtUtc).IsRequired();
        builder.Property(s => s.ExpiresAtUtc).IsRequired();

        // Pilot-scoped idempotency substitute (see VerificationSession.IdempotencyKey's
        // remarks) — unique per agent when supplied, never enforced globally.
        builder.Property(s => s.IdempotencyKey).HasMaxLength(300);
        builder.HasIndex(s => new { s.AgentEmployeeId, s.IdempotencyKey })
            .IsUnique()
            .HasFilter("[IdempotencyKey] IS NOT NULL");

        builder.HasOne<UnitReference>()
            .WithMany()
            .HasForeignKey(s => s.UnitReferenceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ContactReference>()
            .WithMany()
            .HasForeignKey(s => s.ContactReferenceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

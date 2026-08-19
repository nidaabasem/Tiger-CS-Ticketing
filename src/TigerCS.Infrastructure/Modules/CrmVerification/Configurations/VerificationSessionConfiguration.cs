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

        // Pilot-scoped idempotency substitute (see VerificationSessionAppService's
        // remarks for scope/upgrade path) — unique per agent when supplied,
        // never enforced globally (filtered on IdempotencyKey IS NOT NULL).
        //
        // This filtered unique index is the actual race-safety backstop for
        // VerificationSessionAppService.CreateAndConfirmAsync's
        // check-then-insert (a TOCTOU race on its own): SQL Server enforces
        // it and raises a constraint-violation error on a concurrent double
        // insert, which CrmVerificationUnitOfWork translates to
        // DuplicateWriteException for the app layer to recover from. The EF
        // Core InMemory provider used by this project's test suite does
        // NOT enforce filtered indexes (a documented InMemory limitation,
        // confirmed empirically — see
        // CrmVerificationEndpointsTests.CreateVerificationSession_ConcurrentDuplicateRequests_NeitherCallerCrashes's
        // remarks) — this index's real existence/shape is instead validated
        // against a real SQL Server by the db-migration-validation.yml CI
        // workflow, and the recovery logic it triggers is proven
        // deterministically in VerificationSessionAppServiceTests.
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

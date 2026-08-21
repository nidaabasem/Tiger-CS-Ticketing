using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Infrastructure.Modules.SlaAndEscalation.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.6 (the <c>SlaPolicies</c> half) / MVP-ERD.md §2.6 — 1:1 with <see cref="Priority"/>, identifying, Restrict on delete (neither side is ever deleted).</summary>
public class SlaPolicyConfiguration : IEntityTypeConfiguration<SlaPolicy>
{
    public void Configure(EntityTypeBuilder<SlaPolicy> builder)
    {
        builder.ToTable("SlaPolicies");

        // PriorityId is both PK and FK — the identifying 1:1 of MVP-ERD.md
        // §2.6, which is what makes "every Priority has exactly one
        // SlaPolicy row" a schema fact rather than a convention.
        builder.HasKey(p => p.PriorityId);
        builder.Property(p => p.PriorityId).ValueGeneratedNever();

        builder.Property(p => p.FirstResponseTargetMinutes).IsRequired();
        builder.Property(p => p.ResolutionTargetMinutes).IsRequired();
        builder.Property(p => p.ClockBasis).IsRequired();
        builder.Property(p => p.WarningThresholdPercent).HasPrecision(5, 2).IsRequired();
        builder.Property(p => p.Level2ToGmWindowValue).IsRequired();
        builder.Property(p => p.Level2ToGmWindowUnit).IsRequired();
        builder.Property(p => p.IsActive).IsRequired();

        builder.HasOne<Priority>()
            .WithOne()
            .HasForeignKey<SlaPolicy>(p => p.PriorityId)
            .OnDelete(DeleteBehavior.Restrict);

        // Positive targets, at the database as well as in the constructor:
        // a zero or negative target would produce a due date at or before
        // the clock-start moment, which is a permanently-breached ticket.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_SlaPolicies_PositiveTargets",
            "[FirstResponseTargetMinutes] > 0 AND [ResolutionTargetMinutes] > 0"));
    }
}

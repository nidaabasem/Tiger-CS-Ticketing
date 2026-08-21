using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.SlaAndEscalation.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.15 / MVP-ERD.md §2.15 — immutable SLA-period history; rows are never deleted (ISSUE-023).</summary>
public class TicketSlaInstanceConfiguration : IEntityTypeConfiguration<TicketSlaInstance>
{
    public void Configure(EntityTypeBuilder<TicketSlaInstance> builder)
    {
        builder.ToTable("TicketSlaInstances");

        builder.HasKey(i => i.TicketSlaInstanceId);
        builder.Property(i => i.TicketSlaInstanceId).ValueGeneratedOnAdd();

        builder.Property(i => i.TicketId).IsRequired();
        builder.Property(i => i.PriorityId).IsRequired();
        builder.Property(i => i.PeriodStartAtUtc).IsRequired();
        builder.Property(i => i.PeriodEndAtUtc);
        builder.Property(i => i.FirstResponseDueAtUtc).IsRequired();
        builder.Property(i => i.ResolutionDueAtUtc).IsRequired();
        builder.Property(i => i.FirstResponseBreached).IsRequired();
        builder.Property(i => i.ResolutionBreached).IsRequired();
        builder.Property(i => i.ChangeReason).IsRequired();

        // MVP-ERD.md §2.15's referential-integrity note, verbatim: "Exactly
        // one row per TicketId has PeriodEndAtUtc IS NULL (the current
        // period) — app-enforced, recommended as a filtered unique index."
        // Taking the recommendation: it makes the invariant unbreakable by
        // any future code path, including backlog S-14's priority upgrade,
        // rather than resting on every writer remembering to close the
        // previous period first.
        builder.HasIndex(i => i.TicketId)
            .IsUnique()
            .HasFilter("[PeriodEndAtUtc] IS NULL")
            .HasDatabaseName("UX_TicketSlaInstances_CurrentPeriodPerTicket");

        // The sweep's candidate query (SLA-Architecture.md §14) filters
        // current periods by due timestamp and breach flag.
        builder.HasIndex(i => new { i.PeriodEndAtUtc, i.FirstResponseBreached, i.FirstResponseDueAtUtc })
            .HasDatabaseName("IX_TicketSlaInstances_FirstResponseSweep");
        builder.HasIndex(i => new { i.PeriodEndAtUtc, i.ResolutionBreached, i.ResolutionDueAtUtc })
            .HasDatabaseName("IX_TicketSlaInstances_ResolutionSweep");

        // MVP-ERD.md §2.15: ApprovedByEmployeeId is "Required only when
        // ChangeReason = Downgrade". No pilot code path produces a Downgrade
        // row at all (MVP-Implementation-Backlog.md §0 hard-disables
        // downgrades), so this constraint currently guards a value set
        // nothing can reach — which is exactly why it belongs in the schema
        // rather than in a code comment.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_TicketSlaInstances_DowngradeRequiresApprover",
            "[ChangeReason] <> 3 OR [ApprovedByEmployeeId] IS NOT NULL"));

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_TicketSlaInstances_PeriodOrder",
            "[PeriodEndAtUtc] IS NULL OR [PeriodEndAtUtc] >= [PeriodStartAtUtc]"));

        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(i => i.TicketId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Priority>()
            .WithMany()
            .HasForeignKey(i => i.PriorityId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(i => i.ApprovedByEmployeeId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

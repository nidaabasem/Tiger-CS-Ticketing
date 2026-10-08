using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.SlaAndEscalation.Configurations;

/// <summary>MVP-ERD.md section 2.27 / MVP-Data-Dictionary.md - priority-downgrade requests; rows are never deleted.</summary>
public class PriorityDowngradeRequestConfiguration : IEntityTypeConfiguration<PriorityDowngradeRequest>
{
    public void Configure(EntityTypeBuilder<PriorityDowngradeRequest> builder)
    {
        builder.ToTable("PriorityDowngradeRequests");

        builder.HasKey(r => r.PriorityDowngradeRequestId);
        builder.Property(r => r.PriorityDowngradeRequestId).ValueGeneratedOnAdd();

        builder.Property(r => r.TicketId).IsRequired();
        builder.Property(r => r.CurrentPriorityId).IsRequired();
        builder.Property(r => r.RequestedPriorityId).IsRequired();
        builder.Property(r => r.Reason).IsRequired().HasMaxLength(1000);
        builder.Property(r => r.Status).HasConversion<byte>().IsRequired();
        builder.Property(r => r.RequestedByEmployeeId).IsRequired();
        builder.Property(r => r.RequestedAtUtc).IsRequired();
        builder.Property(r => r.ExpiresAtUtc).IsRequired();
        builder.Property(r => r.DecisionNote).HasMaxLength(1000);
        builder.Property(r => r.RowVersion).IsRowVersion();

        // MVP-ERD.md section 2.27: at most one Pending row per ticket - a
        // filtered unique index behind the service's pre-check.
        builder.HasIndex(r => r.TicketId)
            .IsUnique()
            .HasFilter("[Status] = 1")
            .HasDatabaseName("UX_PriorityDowngradeRequests_OnePendingPerTicket");

        builder.HasIndex(r => new { r.Status, r.ExpiresAtUtc })
            .HasDatabaseName("IX_PriorityDowngradeRequests_StatusExpiry");

        // A real downgrade: the requested id is numerically larger (lower urgency).
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_PriorityDowngradeRequests_IsDowngrade", "[RequestedPriorityId] > [CurrentPriorityId]"));

        // Once decided (Approved/Rejected) the decider is recorded; and the
        // requester is never the decider.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_PriorityDowngradeRequests_DecisionConsistent",
            "([Status] IN (2, 3) AND [DecidedByEmployeeId] IS NOT NULL AND [DecidedByEmployeeId] <> [RequestedByEmployeeId]) "
            + "OR [Status] NOT IN (2, 3)"));

        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(r => r.TicketId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Priority>()
            .WithMany()
            .HasForeignKey(r => r.CurrentPriorityId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Priority>()
            .WithMany()
            .HasForeignKey(r => r.RequestedPriorityId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(r => r.RequestedByEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(r => r.DecidedByEmployeeId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

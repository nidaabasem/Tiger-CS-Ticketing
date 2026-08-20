using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.10 / MVP-ERD.md §2.10 — see Ticket's own remarks for the one confirmed relaxation (nullable Unit/Contact FKs, provisional tickets only).</summary>
public class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("Tickets");

        builder.HasKey(t => t.TicketId);
        builder.Property(t => t.TicketId).ValueGeneratedOnAdd();

        builder.Property(t => t.TicketNumber).HasMaxLength(40).IsRequired();
        builder.HasIndex(t => t.TicketNumber).IsUnique();

        builder.Property(t => t.OriginatingDepartmentId).IsRequired();
        builder.Property(t => t.CurrentDepartmentId).IsRequired();
        builder.Property(t => t.CategoryId).IsRequired();
        builder.Property(t => t.PriorityId).IsRequired();

        builder.Property(t => t.TicketStatus).IsRequired();
        builder.Property(t => t.VerificationStatus).IsRequired();
        builder.Property(t => t.EscalationLevel).IsRequired();
        builder.Property(t => t.SlaState).IsRequired();
        builder.Property(t => t.ResolutionOutcome);

        builder.Property(t => t.RequestSummary).HasMaxLength(2000).IsRequired();
        builder.Property(t => t.ReopenCount).IsRequired();
        builder.Property(t => t.CreatedAtUtc).IsRequired();

        // MVP-Data-Dictionary.md §2.10 — optimistic concurrency for the
        // assignment/transfer/status-change/resolve/close/reconciliation
        // operations this increment adds (S-13). Deferred by the prior
        // increment, which had nothing yet to guard.
        builder.Property(t => t.RowVersion).IsRowVersion();

        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(t => t.OriginatingDepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(t => t.CurrentDepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(t => t.CurrentOwnerEmployeeId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // Nullable — see this type's own remarks (confirmed relaxation for a
        // provisional/PendingCrmVerification ticket only).
        builder.HasOne<UnitReference>()
            .WithMany()
            .HasForeignKey(t => t.UnitReferenceId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ContactReference>()
            .WithMany()
            .HasForeignKey(t => t.ContactReferenceId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(t => t.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Priority>()
            .WithMany()
            .HasForeignKey(t => t.PriorityId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(t => t.DuplicateOfTicketId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

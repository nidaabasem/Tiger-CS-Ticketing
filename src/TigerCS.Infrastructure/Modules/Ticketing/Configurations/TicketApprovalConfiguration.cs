using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>Workflow/Automation phase 3 — approval cycles: append-plus-supersede, decisions write-once, at most one Pending (and one current) cycle per (ticket, type) enforced by filtered unique indexes.</summary>
public class TicketApprovalConfiguration : IEntityTypeConfiguration<TicketApproval>
{
    public void Configure(EntityTypeBuilder<TicketApproval> builder)
    {
        builder.ToTable("TicketApprovals");

        builder.HasKey(a => a.TicketApprovalId);
        builder.Property(a => a.TicketApprovalId).ValueGeneratedOnAdd();

        builder.Property(a => a.ApprovalType).HasConversion<byte>().IsRequired();
        builder.Property(a => a.Status).HasConversion<byte>().IsRequired();
        builder.Property(a => a.TargetKind).HasConversion<byte>().IsRequired();
        builder.Property(a => a.TargetRoleName).HasMaxLength(64);
        builder.Property(a => a.RequestComment).HasMaxLength(1000);
        builder.Property(a => a.DecisionComment).HasMaxLength(1000);
        builder.Property(a => a.IsCurrent).IsRequired();

        // No two simultaneously active (Pending) cycles of the same type on
        // one ticket — a database guarantee behind the service's pre-check.
        builder.HasIndex(a => new { a.TicketId, a.ApprovalType }, "UX_TicketApprovals_OnePendingPerType")
            .HasFilter("[Status] = 1")
            .IsUnique();

        // Exactly one row per (ticket, type) is the current cycle.
        builder.HasIndex(a => new { a.TicketId, a.ApprovalType }, "UX_TicketApprovals_OneCurrentPerType")
            .HasFilter("[IsCurrent] = 1")
            .IsUnique();

        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(a => a.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(a => a.TargetDepartmentId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(a => a.TargetEmployeeId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(a => a.RequestedByEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(a => a.DecidedByEmployeeId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Workflow/Automation phase 3 — the typed, append-only workflow event store phase 4's SLA triggers read.</summary>
public class TicketWorkflowEventConfiguration : IEntityTypeConfiguration<TicketWorkflowEvent>
{
    public void Configure(EntityTypeBuilder<TicketWorkflowEvent> builder)
    {
        builder.ToTable("TicketWorkflowEvents");

        builder.HasKey(e => e.TicketWorkflowEventId);
        builder.Property(e => e.TicketWorkflowEventId).ValueGeneratedOnAdd();

        builder.Property(e => e.EventType).HasConversion<byte>().IsRequired();
        builder.Property(e => e.Note).HasMaxLength(500);

        // The phase-4 read path: "the first event of this type for this
        // ticket" (a conditional SLA trigger's clock-start lookup).
        builder.HasIndex(e => new { e.TicketId, e.EventType });

        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(e => e.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<TicketApproval>()
            .WithMany()
            .HasForeignKey(e => e.TicketApprovalId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(e => e.ActorEmployeeId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

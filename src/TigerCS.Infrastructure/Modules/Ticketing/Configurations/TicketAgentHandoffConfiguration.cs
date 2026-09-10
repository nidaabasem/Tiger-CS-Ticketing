using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>
/// Pending human work raised from a customer interaction, on any channel.
///
/// <para>
/// The load-bearing constraint is the filtered unique index over
/// (<c>TicketInteractionId</c>) where the work is still unresolved: a
/// redelivered Genesys handoff event cannot produce a second pending work
/// item for the same conversation, and that holds under concurrent delivery
/// rather than only under sequential retries — the same shape as the
/// conversation-id uniqueness behind "one inquiry, one ticket".
/// </para>
/// </summary>
public class TicketAgentHandoffConfiguration : IEntityTypeConfiguration<TicketAgentHandoff>
{
    public void Configure(EntityTypeBuilder<TicketAgentHandoff> builder)
    {
        builder.ToTable("TicketAgentHandoffs");

        builder.HasKey(h => h.TicketAgentHandoffId);
        builder.Property(h => h.TicketAgentHandoffId).ValueGeneratedOnAdd();

        builder.Property(h => h.TicketId).IsRequired();
        builder.Property(h => h.TicketInteractionId).IsRequired();
        builder.Property(h => h.DepartmentId).IsRequired();
        builder.Property(h => h.ChannelId).IsRequired();
        builder.Property(h => h.Status).HasConversion<byte>().IsRequired();

        // Nullable on purpose: how a channel continues is Genesys' behaviour
        // to state, and it has not stated it. Null means "not stated", never
        // a channel-derived guess.
        builder.Property(h => h.Mode).HasConversion<byte?>();

        builder.Property(h => h.RequestReason).HasMaxLength(TicketAgentHandoff.RequestReasonMaxLength);
        builder.Property(h => h.ResolutionNote).HasMaxLength(TicketAgentHandoff.ResolutionNoteMaxLength);

        // External identifiers, stored as strings — never foreign keys.
        builder.Property(h => h.ExternalWorkItemId).HasMaxLength(TicketAgentHandoff.ExternalIdMaxLength);
        builder.Property(h => h.GenesysAgentId).HasMaxLength(TicketAgentHandoff.ExternalIdMaxLength);

        builder.Property(h => h.RequestedAtUtc).IsRequired();
        builder.Property(h => h.CreatedAtUtc).IsRequired();

        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(h => h.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<TicketInteraction>()
            .WithMany()
            .HasForeignKey(h => h.TicketInteractionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(h => h.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Channel>()
            .WithMany()
            .HasForeignKey(h => h.ChannelId)
            .OnDelete(DeleteBehavior.Restrict);

        // The idempotency guarantee: at most ONE unresolved handoff per
        // interaction. A retried "human required" event for a conversation
        // whose work is still pending loses this race and is answered with
        // the existing work item.
        builder.HasIndex(h => h.TicketInteractionId, "UX_TicketAgentHandoffs_OpenPerInteraction")
            .HasFilter("[ResolvedAtUtc] IS NULL")
            .IsUnique();

        // Genesys' own work-item id when it supplies one — a second, stronger
        // idempotency key that costs nothing while it is absent.
        builder.HasIndex(h => h.ExternalWorkItemId, "UX_TicketAgentHandoffs_ExternalWorkItemId")
            .HasFilter("[ExternalWorkItemId] IS NOT NULL")
            .IsUnique();

        // The agent work list: outstanding work in a department, oldest wait
        // first. Filtered so the index carries only rows the list can show.
        builder.HasIndex(h => new { h.DepartmentId, h.RequestedAtUtc }, "IX_TicketAgentHandoffs_OpenByDepartment")
            .HasFilter("[ResolvedAtUtc] IS NULL");

        // "What is waiting for me" and the ticket's own handoff history.
        builder.HasIndex(h => h.AssignedEmployeeId, "IX_TicketAgentHandoffs_AssignedEmployeeId");
        builder.HasIndex(h => h.TicketId, "IX_TicketAgentHandoffs_TicketId");
    }
}

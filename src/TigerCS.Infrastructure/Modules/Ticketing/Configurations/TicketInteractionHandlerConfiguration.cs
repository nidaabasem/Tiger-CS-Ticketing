using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Identity;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>
/// Every Ticketing user who actually handled a Genesys interaction — append-only, unique per (interaction, user). The
/// (HandledByUserId, TicketId) index is the CS Agent visibility predicate's access path.
/// </summary>
public class TicketInteractionHandlerConfiguration : IEntityTypeConfiguration<TicketInteractionHandler>
{
    public void Configure(EntityTypeBuilder<TicketInteractionHandler> builder)
    {
        builder.ToTable("TicketInteractionHandlers");

        builder.HasKey(h => h.TicketInteractionHandlerId);
        builder.Property(h => h.TicketInteractionHandlerId).ValueGeneratedOnAdd();

        builder.Property(h => h.GenesysAgentUserId).HasMaxLength(TicketInteractionHandler.GenesysAgentUserIdMaxLength).IsRequired();
        builder.Property(h => h.Source).HasConversion<byte>().IsRequired();

        // One row per user per interaction — a redelivered or repeated event, or the same agent reconnecting after a hold, is a no-op.
        builder.HasIndex(h => new { h.TicketInteractionId, h.HandledByUserId }, "UX_TicketInteractionHandlers_InteractionId_UserId")
            .IsUnique();

        // "Which tickets did this user handle" — the CS Agent visibility predicate.
        builder.HasIndex(h => new { h.HandledByUserId, h.TicketId }, "IX_TicketInteractionHandlers_HandledByUserId_TicketId");

        builder.HasOne<TicketInteraction>()
            .WithMany()
            .HasForeignKey(h => h.TicketInteractionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, not cascade: a second cascade path Tickets → Interactions → Handlers and Tickets → Handlers is rejected by SQL Server, and
        // tickets are never deleted anyway.
        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(h => h.TicketId)
            .OnDelete(DeleteBehavior.Restrict);

        // History outlives a user (deactivation is the only retirement path).
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(h => h.HandledByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

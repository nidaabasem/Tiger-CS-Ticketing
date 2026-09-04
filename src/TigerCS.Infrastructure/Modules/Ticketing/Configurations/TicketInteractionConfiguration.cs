using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>
/// Architectural hardening (pre-phase-3): a ticket has MANY interactions
/// over its lifetime — one row each, append-only, Genesys fields nullable by
/// design, with at most one originating interaction per ticket enforced by a
/// filtered unique index.
/// </summary>
public class TicketInteractionConfiguration : IEntityTypeConfiguration<TicketInteraction>
{
    public void Configure(EntityTypeBuilder<TicketInteraction> builder)
    {
        builder.ToTable("TicketInteractions");

        builder.HasKey(i => i.TicketInteractionId);
        builder.Property(i => i.TicketInteractionId).ValueGeneratedOnAdd();

        builder.Property(i => i.IsOriginatingInteraction).IsRequired();
        builder.Property(i => i.Source).HasConversion<byte>().IsRequired();
        builder.Property(i => i.ChannelId).HasConversion<byte>().IsRequired();
        builder.Property(i => i.CustomerPhone).HasMaxLength(32).IsRequired();
        builder.Property(i => i.CalledNumber).HasMaxLength(32);

        // Genesys identifiers are external identifiers stored as strings —
        // never foreign keys (there is nothing local to reference).
        builder.Property(i => i.GenesysConversationId).HasMaxLength(64);
        builder.Property(i => i.GenesysQueueId).HasMaxLength(64);
        builder.Property(i => i.GenesysQueueName).HasMaxLength(200);
        builder.Property(i => i.GenesysAgentId).HasMaxLength(64);
        builder.Property(i => i.GenesysAgentName).HasMaxLength(200);
        builder.Property(i => i.Direction).HasMaxLength(32);

        // Two distinct indexes over TicketId — both created via the
        // named-index overload, because an unnamed HasIndex on the same
        // column set would re-configure the first index rather than add a
        // second one.
        // 1) The ticket's interaction list read path.
        builder.HasIndex(i => i.TicketId, "IX_TicketInteractions_TicketId");

        // 2) At most one originating interaction per ticket — a database
        // guarantee, not a code hope.
        builder.HasIndex(i => i.TicketId, "UX_TicketInteractions_OneOriginatingPerTicket")
            .HasFilter("[IsOriginatingInteraction] = 1")
            .IsUnique();

        // Ticket ↔ Genesys conversation traceability in the other direction:
        // find the ticket(s)/interaction(s) of one Genesys conversation.
        builder.HasIndex(i => i.GenesysConversationId)
            .HasFilter("[GenesysConversationId] IS NOT NULL");

        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(i => i.TicketId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

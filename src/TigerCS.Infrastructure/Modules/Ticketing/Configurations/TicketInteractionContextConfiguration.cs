using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>Workflow/Automation phase 2 — the interaction context a ticket was created from; at most one row per ticket, write-once, Genesys fields nullable by design.</summary>
public class TicketInteractionContextConfiguration : IEntityTypeConfiguration<TicketInteractionContext>
{
    public void Configure(EntityTypeBuilder<TicketInteractionContext> builder)
    {
        builder.ToTable("TicketInteractionContexts");

        builder.HasKey(c => c.TicketId);
        builder.Property(c => c.TicketId).ValueGeneratedNever();

        builder.Property(c => c.Source).HasConversion<byte>().IsRequired();
        builder.Property(c => c.ChannelId).HasConversion<byte>().IsRequired();
        builder.Property(c => c.CustomerPhone).HasMaxLength(32).IsRequired();
        builder.Property(c => c.CalledNumber).HasMaxLength(32);

        // Genesys identifiers are external identifiers stored as strings —
        // never foreign keys (there is nothing local to reference).
        builder.Property(c => c.GenesysConversationId).HasMaxLength(64);
        builder.Property(c => c.GenesysQueueId).HasMaxLength(64);
        builder.Property(c => c.GenesysQueueName).HasMaxLength(200);
        builder.Property(c => c.GenesysAgentId).HasMaxLength(64);
        builder.Property(c => c.GenesysAgentName).HasMaxLength(200);
        builder.Property(c => c.Direction).HasMaxLength(32);

        // Ticket ↔ Genesys conversation traceability in the other direction:
        // find the ticket(s) created from one Genesys conversation.
        builder.HasIndex(c => c.GenesysConversationId)
            .HasFilter("[GenesysConversationId] IS NOT NULL");

        builder.HasOne<Ticket>()
            .WithOne()
            .HasForeignKey<TicketInteractionContext>(c => c.TicketId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

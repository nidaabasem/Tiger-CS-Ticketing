using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>
/// Genesys integration phase 1 — the structured transcript of a text
/// conversation: one row per message, belonging to one
/// <see cref="TicketInteraction"/>.
///
/// <para>
/// <b>Cascade from the interaction, unlike most of this schema.</b>
/// Interactions themselves already cascade from their ticket, and a
/// transcript has no meaning at all without the conversation it transcribes
/// — an orphaned message row could never be read back or attributed. Ticket
/// data itself is still never deleted (the 7-year retention posture is
/// unchanged); this only settles what happens if an interaction ever is.
/// </para>
/// </summary>
public class TicketInteractionMessageConfiguration : IEntityTypeConfiguration<TicketInteractionMessage>
{
    public void Configure(EntityTypeBuilder<TicketInteractionMessage> builder)
    {
        builder.ToTable("TicketInteractionMessages");

        builder.HasKey(m => m.TicketInteractionMessageId);
        builder.Property(m => m.TicketInteractionMessageId).ValueGeneratedOnAdd();

        builder.Property(m => m.Sequence).IsRequired();
        builder.Property(m => m.Sender).HasConversion<byte>().IsRequired();
        builder.Property(m => m.SenderName).HasMaxLength(TicketInteractionMessage.SenderNameMaxLength);
        builder.Property(m => m.SenderId).HasMaxLength(TicketInteractionMessage.SenderIdMaxLength);
        builder.Property(m => m.ExternalMessageId).HasMaxLength(TicketInteractionMessage.ExternalMessageIdMaxLength);
        builder.Property(m => m.SentAtUtc).IsRequired();

        // No max length: a chat message is free customer/agent text and
        // truncating a transcript would destroy the record this table exists
        // to preserve. nvarchar(max) is the correct trade here.
        builder.Property(m => m.Body).IsRequired();

        // The transcript read path — every message of one interaction, in
        // order — is the only way this table is ever queried.
        builder.HasIndex(m => new { m.TicketInteractionId, m.Sequence })
            .IsUnique()
            .HasDatabaseName("UX_TicketInteractionMessages_InteractionSequence");

        builder.HasOne<TicketInteraction>()
            .WithMany()
            .HasForeignKey(m => m.TicketInteractionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

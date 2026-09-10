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
        builder.Property(i => i.ChannelId).IsRequired();
        builder.Property(i => i.CustomerPhone).HasMaxLength(32).IsRequired();
        builder.Property(i => i.CalledNumber).HasMaxLength(32);

        // Genesys integration phase 1 — what the channel collected about the
        // customer (a website chat form's Full Name/Email), and the
        // conversation's own ending. All nullable: a voice call collects no
        // form fields, and a conversation that is still live has no end.
        builder.Property(i => i.CustomerName).HasMaxLength(TicketInteraction.CustomerNameMaxLength);
        builder.Property(i => i.CustomerEmail).HasMaxLength(TicketInteraction.CustomerEmailMaxLength);
        builder.Property(i => i.EndReason).HasMaxLength(TicketInteraction.EndReasonMaxLength);
        builder.Property(i => i.EndedAtUtc);

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
        // find the interaction of one Genesys conversation — and, since the
        // Genesys integration phase, guarantee there is at most ONE.
        //
        // This UNIQUE filtered index is the database-level half of "one
        // Genesys inquiry produces exactly one ticket": the ingestion service
        // checks for an existing conversation first, but two concurrent
        // deliveries of the same conversation can both pass that read. The
        // index makes the loser fail its insert (translated to
        // DuplicateWriteException), which ingestion answers with the winner's
        // ticket — so a retried or duplicated Genesys event can never produce
        // a second ticket, rather than merely being unlikely to.
        //
        // Filtered on NOT NULL because locally-created (walk-in) interactions
        // carry no conversation id, and SQL Server treats multiple NULLs as
        // duplicates in a unique index.
        builder.HasIndex(i => i.GenesysConversationId, "UX_TicketInteractions_GenesysConversationId")
            .HasFilter("[GenesysConversationId] IS NOT NULL")
            .IsUnique();

        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(i => i.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        // The interaction's channel is configuration (Channels) — Restrict:
        // a channel referenced by history is deactivated, never deleted.
        builder.HasOne<Channel>()
            .WithMany()
            .HasForeignKey(i => i.ChannelId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

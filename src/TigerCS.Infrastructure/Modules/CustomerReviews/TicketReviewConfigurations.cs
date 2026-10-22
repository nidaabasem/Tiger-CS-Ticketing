using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.CustomerReviews;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.CustomerReviews;

public class TicketReviewInvitationConfiguration : IEntityTypeConfiguration<TicketReviewInvitation>
{
    public void Configure(EntityTypeBuilder<TicketReviewInvitation> builder)
    {
        builder.ToTable("TicketReviewInvitations");

        builder.HasKey(i => i.TicketReviewInvitationId);
        builder.Property(i => i.TicketReviewInvitationId).ValueGeneratedOnAdd();

        builder.Property(i => i.TicketId).IsRequired();
        builder.Property(i => i.ReopenCycle).IsRequired();

        // SHA-256 of the random token. The token itself is never stored.
        builder.Property(i => i.TokenHash).IsRequired().HasColumnType("varbinary(32)");
        builder.Property(i => i.IsTest).IsRequired();
        builder.Property(i => i.CreatedAtUtc).IsRequired();
        builder.Property(i => i.ExpiresAtUtc).IsRequired();

        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(i => i.TicketId)
            .OnDelete(DeleteBehavior.Restrict);

        // The lookup key for the public review page; a hash that does not exist is simply an invalid link.
        builder.HasIndex(i => i.TokenHash).IsUnique().HasDatabaseName("UX_TicketReviewInvitations_TokenHash");
        builder.HasIndex(i => new { i.TicketId, i.ReopenCycle }).HasDatabaseName("IX_TicketReviewInvitations_TicketId_ReopenCycle");
    }
}

public class TicketReviewConfiguration : IEntityTypeConfiguration<TicketReview>
{
    public void Configure(EntityTypeBuilder<TicketReview> builder)
    {
        builder.ToTable("TicketReviews", t => t.HasCheckConstraint("CK_TicketReviews_Rating", "[Rating] BETWEEN 1 AND 5"));

        builder.HasKey(r => r.TicketReviewId);
        builder.Property(r => r.TicketReviewId).ValueGeneratedOnAdd();

        builder.Property(r => r.TicketId).IsRequired();
        builder.Property(r => r.ReopenCycle).IsRequired();
        builder.Property(r => r.TicketReviewInvitationId).IsRequired();
        builder.Property(r => r.IsTest).IsRequired();
        builder.Property(r => r.Rating).IsRequired();
        builder.Property(r => r.Comment).HasMaxLength(TicketReview.CommentMaxLength);
        builder.Property(r => r.SubmittedAtUtc).IsRequired();

        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(r => r.TicketId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<TicketReviewInvitation>()
            .WithMany()
            .HasForeignKey(r => r.TicketReviewInvitationId)
            .OnDelete(DeleteBehavior.Restrict);

        // One review per ticket cycle and environment kind: the database, not the application, is what stops two
        // simultaneous submissions (or two emails' links) from both being stored.
        builder.HasIndex(r => new { r.TicketId, r.ReopenCycle, r.IsTest })
            .IsUnique()
            .HasDatabaseName("UX_TicketReviews_TicketId_ReopenCycle_IsTest");

        // Production reporting always filters IsTest = 0.
        builder.HasIndex(r => new { r.IsTest, r.SubmittedAtUtc }).HasDatabaseName("IX_TicketReviews_IsTest_SubmittedAtUtc");
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Notifications;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Notifications.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.21 / MVP-ERD.md §2.21.</summary>
public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications");

        builder.HasKey(n => n.NotificationId);
        builder.Property(n => n.NotificationId).ValueGeneratedOnAdd();

        builder.Property(n => n.TicketId);
        builder.Property(n => n.NotificationType).HasConversion<byte>().IsRequired();
        builder.Property(n => n.RecipientEmployeeId);

        // nvarchar(320) — RFC 5321's maximum addressable length (64 local +
        // @ + 255 domain), exactly as §2.21 specifies.
        builder.Property(n => n.RecipientAddress).HasMaxLength(320);

        builder.Property(n => n.Channel).HasConversion<byte>().IsRequired();
        builder.Property(n => n.DeliveryStatus).HasConversion<byte>().IsRequired();
        builder.Property(n => n.CorrelationId).IsRequired();
        builder.Property(n => n.OutboxMessageId);
        builder.Property(n => n.RetryCount).IsRequired();
        builder.Property(n => n.CreatedAtUtc).IsRequired();

        // MVP-ERD.md §2.21: "Tickets -> Notifications, 1:many, Optional,
        // Restrict/never deleted".
        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(n => n.TicketId)
            .OnDelete(DeleteBehavior.Restrict);

        // MVP-ERD.md §2.21: "Notifications -> OutboxMessages, many:1,
        // Required for any notification actually dispatched ..., Restrict".
        // Modelled optional at the database because that same clause allows a
        // Pending row to exist briefly before its Outbox link; every row this
        // increment writes carries one.
        builder.HasOne<OutboxMessage>()
            .WithMany()
            .HasForeignKey(n => n.OutboxMessageId)
            .OnDelete(DeleteBehavior.Restrict);

        // One notification per Outbox message per type: the handler's
        // find-or-create on retry resolves through this, and the unique index
        // is what stops a genuine race between two dispatch attempts from
        // producing two delivery rows for one event. Filtered because
        // OutboxMessageId is nullable and SQL Server treats multiple NULLs as
        // duplicates in a unique index.
        builder.HasIndex(n => new { n.OutboxMessageId, n.NotificationType })
            .IsUnique()
            .HasDatabaseName("UX_Notifications_OutboxMessageId_NotificationType")
            .HasFilter("[OutboxMessageId] IS NOT NULL");

        builder.HasIndex(n => n.TicketId).HasDatabaseName("IX_Notifications_TicketId");

        // FR-NOT-05's operational queue — failed and dead-lettered sends.
        builder.HasIndex(n => n.DeliveryStatus).HasDatabaseName("IX_Notifications_DeliveryStatus");
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.13 / MVP-ERD.md §2.13 (ADR-0018) — append-only, no update/delete path.</summary>
public class TicketStatusHistoryConfiguration : IEntityTypeConfiguration<TicketStatusHistory>
{
    public void Configure(EntityTypeBuilder<TicketStatusHistory> builder)
    {
        builder.ToTable("TicketStatusHistory");

        builder.HasKey(h => h.TicketStatusHistoryId);
        builder.Property(h => h.TicketStatusHistoryId).ValueGeneratedOnAdd();

        builder.Property(h => h.Dimension).IsRequired();
        builder.Property(h => h.NewValue).IsRequired();
        builder.Property(h => h.ActorIsSystem).IsRequired();
        builder.Property(h => h.Note).HasMaxLength(1000);
        builder.Property(h => h.CorrelationId).IsRequired();
        builder.Property(h => h.OccurredAtUtc).IsRequired();

        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(h => h.TicketId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(h => h.ActorEmployeeId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

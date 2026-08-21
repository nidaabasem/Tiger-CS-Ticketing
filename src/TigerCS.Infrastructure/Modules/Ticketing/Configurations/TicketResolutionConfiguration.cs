using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.14 / MVP-ERD.md §2.14 — one row per resolve cycle; append-only.</summary>
public class TicketResolutionConfiguration : IEntityTypeConfiguration<TicketResolution>
{
    public void Configure(EntityTypeBuilder<TicketResolution> builder)
    {
        builder.ToTable("TicketResolutions");

        builder.HasKey(r => r.TicketResolutionId);
        builder.Property(r => r.TicketResolutionId).ValueGeneratedOnAdd();

        builder.Property(r => r.ResolutionOutcome).IsRequired();
        builder.Property(r => r.ResolutionNote).HasMaxLength(4000).IsRequired();
        builder.Property(r => r.ResolvedAtUtc).IsRequired();
        builder.Property(r => r.IsCurrent).IsRequired();

        // MVP-ERD.md §2.14: "Optional until first resolution; required
        // before Closed" — one current resolution per ticket.
        builder.HasIndex(r => r.TicketId).IsUnique().HasFilter("[IsCurrent] = 1");

        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(r => r.TicketId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(r => r.ResolvingEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        // Self-referencing — MVP-ERD.md §2.10's duplicate-chain rule applies
        // here too (enforced in the application/domain layer, not the DB).
        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(r => r.DuplicateOfTicketId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

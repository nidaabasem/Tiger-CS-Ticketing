using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.9 / MVP-ERD.md §2.9.</summary>
public class IntakeRecordConfiguration : IEntityTypeConfiguration<IntakeRecord>
{
    public void Configure(EntityTypeBuilder<IntakeRecord> builder)
    {
        builder.ToTable("IntakeRecords");

        builder.HasKey(i => i.IntakeRecordId);
        builder.Property(i => i.IntakeRecordId).ValueGeneratedOnAdd();

        builder.Property(i => i.ChannelId).IsRequired();
        builder.Property(i => i.ReceivedAtUtc).IsRequired();
        builder.Property(i => i.PhoneNumber).HasMaxLength(30).IsRequired();
        builder.Property(i => i.IsUnitRelated).IsRequired();
        builder.Property(i => i.RawUnitNumberEntered).HasMaxLength(50);
        builder.Property(i => i.CrmVerificationStatus).IsRequired();
        builder.Property(i => i.CreatedByEmployeeId).IsRequired();

        builder.HasOne<Priority>()
            .WithMany()
            .HasForeignKey(i => i.PriorityHint)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // Employees are never hard-deleted (MVP-ERD.md §2.2) — Restrict is
        // never actually exercised, consistent with every other
        // Employees-referencing FK in this codebase.
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(i => i.CreatedByEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(i => i.LinkedTicketId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

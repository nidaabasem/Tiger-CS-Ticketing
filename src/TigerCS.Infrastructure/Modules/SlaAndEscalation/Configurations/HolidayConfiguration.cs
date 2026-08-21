using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Infrastructure.Modules.SlaAndEscalation.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.20 — "HolidayDate ... Unique per calendar"; never deleted, for historical accuracy (MVP-ERD.md §2.20).</summary>
public class HolidayConfiguration : IEntityTypeConfiguration<Holiday>
{
    public void Configure(EntityTypeBuilder<Holiday> builder)
    {
        builder.ToTable("Holidays");

        builder.HasKey(h => h.HolidayId);
        builder.Property(h => h.HolidayId).ValueGeneratedOnAdd();

        builder.Property(h => h.BusinessCalendarId).IsRequired();
        builder.Property(h => h.HolidayDate).HasColumnType("date").IsRequired();
        builder.Property(h => h.Description).HasMaxLength(200);
        builder.Property(h => h.EnteredByEmployeeId).IsRequired();

        // MVP-API-Contracts.md §5.11's `409` on a duplicate date, made a
        // schema fact rather than a race-prone read-then-insert.
        builder.HasIndex(h => new { h.BusinessCalendarId, h.HolidayDate }).IsUnique();

        builder.HasOne<BusinessCalendar>()
            .WithMany()
            .HasForeignKey(h => h.BusinessCalendarId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(h => h.EnteredByEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(h => h.ConfirmedByEmployeeId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

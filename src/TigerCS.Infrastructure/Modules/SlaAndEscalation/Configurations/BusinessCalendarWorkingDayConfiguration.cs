using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Infrastructure.Modules.SlaAndEscalation.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.20 — exactly seven rows per calendar (MVP-ERD.md §2.20's 1:7 cardinality), enforced here as one row per <c>DayOfWeek</c> value.</summary>
public class BusinessCalendarWorkingDayConfiguration : IEntityTypeConfiguration<BusinessCalendarWorkingDay>
{
    public void Configure(EntityTypeBuilder<BusinessCalendarWorkingDay> builder)
    {
        builder.ToTable("BusinessCalendarWorkingDays");

        builder.HasKey(d => d.BusinessCalendarWorkingDayId);
        builder.Property(d => d.BusinessCalendarWorkingDayId).ValueGeneratedOnAdd();

        builder.Property(d => d.BusinessCalendarId).IsRequired();
        builder.Property(d => d.DayOfWeek).IsRequired();
        builder.Property(d => d.IsWorkingDay).IsRequired();

        // The database half of MVP-ERD.md §2.20's "exactly 7 rows (one per
        // DayOfWeek) per calendar": a duplicate day is impossible, and the
        // range check keeps the value inside DayOfWeek's own domain. The
        // "exactly seven, none missing" half stays app-enforced, as §2.20
        // says — a CHECK constraint cannot count rows.
        builder.HasIndex(d => new { d.BusinessCalendarId, d.DayOfWeek }).IsUnique();

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_BusinessCalendarWorkingDays_DayOfWeekRange", "[DayOfWeek] BETWEEN 0 AND 6"));

        builder.HasOne<BusinessCalendar>()
            .WithMany()
            .HasForeignKey(d => d.BusinessCalendarId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

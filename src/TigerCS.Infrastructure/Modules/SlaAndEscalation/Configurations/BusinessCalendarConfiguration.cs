using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Infrastructure.Modules.SlaAndEscalation.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.20 / ADR-0010 — the working pattern and business-day window, held as reference data rather than in code.</summary>
public class BusinessCalendarConfiguration : IEntityTypeConfiguration<BusinessCalendar>
{
    public void Configure(EntityTypeBuilder<BusinessCalendar> builder)
    {
        builder.ToTable("BusinessCalendars");

        builder.HasKey(c => c.BusinessCalendarId);
        builder.Property(c => c.BusinessCalendarId).ValueGeneratedOnAdd();

        builder.Property(c => c.Name).HasMaxLength(100).IsRequired();

        // `time` in SQL Server, matching MVP-Data-Dictionary.md §2.20's
        // declared column type — a wall-clock time of day in the calendar's
        // own zone, deliberately not an instant.
        builder.Property(c => c.BusinessDayStartLocal).HasColumnType("time").IsRequired();
        builder.Property(c => c.BusinessDayEndLocal).HasColumnType("time").IsRequired();

        builder.Property(c => c.TimeZone).HasMaxLength(50).IsRequired();
        builder.Property(c => c.EffectiveFromUtc).IsRequired();
        builder.Property(c => c.IsActive).IsRequired();

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_BusinessCalendars_WindowOrder",
            "[BusinessDayEndLocal] > [BusinessDayStartLocal]"));
    }
}

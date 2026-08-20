using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Infrastructure.Modules.SlaAndEscalation.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.6 (Priorities half only — see Priority's own remarks).</summary>
public class PriorityConfiguration : IEntityTypeConfiguration<Priority>
{
    public void Configure(EntityTypeBuilder<Priority> builder)
    {
        builder.ToTable("Priorities");

        builder.HasKey(p => p.PriorityId);
        builder.Property(p => p.PriorityId).ValueGeneratedNever();

        builder.Property(p => p.Name).HasMaxLength(20).IsRequired();
        builder.Property(p => p.DisplayOrder).IsRequired();
    }
}

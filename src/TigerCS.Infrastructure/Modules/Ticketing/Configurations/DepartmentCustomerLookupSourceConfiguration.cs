using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>The Department → customer-lookup-source mapping — see DepartmentCustomerLookupSource's own remarks.</summary>
public class DepartmentCustomerLookupSourceConfiguration : IEntityTypeConfiguration<DepartmentCustomerLookupSource>
{
    public void Configure(EntityTypeBuilder<DepartmentCustomerLookupSource> builder)
    {
        builder.ToTable("DepartmentCustomerLookupSources");

        builder.HasKey(d => d.DepartmentCustomerLookupSourceId);
        builder.Property(d => d.DepartmentCustomerLookupSourceId).ValueGeneratedOnAdd();

        builder.Property(d => d.DepartmentId).IsRequired();
        builder.Property(d => d.Source).IsRequired();

        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(d => d.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        // One row per (Department, Source) — a department is never
        // configured to search the same source twice.
        builder.HasIndex(d => new { d.DepartmentId, d.Source }).IsUnique();
    }
}

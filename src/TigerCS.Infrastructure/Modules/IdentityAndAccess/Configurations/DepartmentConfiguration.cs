using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.3 / MVP-ERD.md §2.3.</summary>
public class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> builder)
    {
        builder.ToTable("Departments");

        builder.HasKey(d => d.DepartmentId);
        builder.Property(d => d.DepartmentId).ValueGeneratedOnAdd();

        builder.Property(d => d.Name).HasMaxLength(100).IsRequired();
        builder.HasIndex(d => d.Name).IsUnique();

        builder.Property(d => d.Code).HasMaxLength(10).IsRequired();
        builder.HasIndex(d => d.Code).IsUnique();

        builder.Property(d => d.IsActive).IsRequired();
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Infrastructure.Modules.ClassificationAndRouting.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.5 / MVP-ERD.md §2.5.</summary>
public class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories");

        builder.HasKey(c => c.CategoryId);
        builder.Property(c => c.CategoryId).ValueGeneratedOnAdd();

        builder.Property(c => c.Name).HasMaxLength(100).IsRequired();
        builder.Property(c => c.DepartmentId).IsRequired();
        builder.Property(c => c.IsActive).IsRequired();

        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(c => c.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Self-ref parent — MVP-ERD.md §2.5: "a sub-category's parent must
        // itself have ParentCategoryId IS NULL" is app-enforced, not modeled
        // as a DB constraint here.
        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(c => c.ParentCategoryId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

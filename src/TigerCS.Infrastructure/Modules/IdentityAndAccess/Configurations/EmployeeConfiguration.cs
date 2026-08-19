using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Identity;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.2 / MVP-ERD.md §2.2.</summary>
public class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        builder.ToTable("Employees");

        builder.HasKey(e => e.EmployeeId);
        builder.Property(e => e.EmployeeId).ValueGeneratedNever();

        builder.Property(e => e.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.IsGeynessStaff).IsRequired();
        builder.Property(e => e.DeactivatedAtUtc);
        builder.Property(e => e.CreatedAtUtc).IsRequired();

        // Employees.EmployeeId is both PK and FK -> AspNetUsers.Id (identifying
        // 1:1 relationship, ADR-0004). No navigation on the Employee side to
        // keep Domain framework-agnostic; the FK is configured from this side only.
        builder.HasOne<ApplicationUser>()
            .WithOne()
            .HasForeignKey<Employee>(e => e.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired();

        builder.HasMany(e => e.DepartmentAssignments)
            .WithOne(a => a.Employee)
            .HasForeignKey(a => a.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

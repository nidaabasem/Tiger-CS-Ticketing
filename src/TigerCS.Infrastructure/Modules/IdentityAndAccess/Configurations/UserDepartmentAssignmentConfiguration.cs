using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.4 / MVP-ERD.md §2.4.</summary>
public class UserDepartmentAssignmentConfiguration : IEntityTypeConfiguration<UserDepartmentAssignment>
{
    public void Configure(EntityTypeBuilder<UserDepartmentAssignment> builder)
    {
        builder.ToTable("UserDepartmentAssignments");

        builder.HasKey(a => a.UserDepartmentAssignmentId);
        builder.Property(a => a.UserDepartmentAssignmentId).ValueGeneratedOnAdd();

        builder.Property(a => a.EmployeeId).IsRequired();
        builder.Property(a => a.DepartmentId).IsRequired();
        builder.Property(a => a.IsPrimary).IsRequired();
        builder.Property(a => a.AssignedAtUtc).IsRequired();
        builder.Property(a => a.AssignedByEmployeeId);

        builder.HasOne(a => a.Department)
            .WithMany()
            .HasForeignKey(a => a.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        // No duplicate (Employee, Department) row — backs
        // IUserDepartmentAssignmentRepository.ExistsAsync at the DB level too.
        builder.HasIndex(a => new { a.EmployeeId, a.DepartmentId }).IsUnique();

        // "Exactly one true row per EmployeeId" (MVP-Data-Dictionary.md §2.4) —
        // the recommended filtered-unique-index DB backstop for the app-enforced
        // "at most one primary department" invariant.
        builder.HasIndex(a => a.EmployeeId)
            .HasDatabaseName("IX_UserDepartmentAssignments_EmployeeId_PrimaryOnly")
            .IsUnique()
            .HasFilter("[IsPrimary] = 1");
    }
}

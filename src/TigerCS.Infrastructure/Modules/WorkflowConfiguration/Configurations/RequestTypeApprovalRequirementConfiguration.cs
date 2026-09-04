using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Configurations;

/// <summary>Workflow/Automation phase 3 — the per-request-type approval requirement configuration; at most one requirement per (request type, approval type).</summary>
public class RequestTypeApprovalRequirementConfiguration : IEntityTypeConfiguration<RequestTypeApprovalRequirement>
{
    public void Configure(EntityTypeBuilder<RequestTypeApprovalRequirement> builder)
    {
        builder.ToTable("RequestTypeApprovalRequirements");

        builder.HasKey(r => r.RequestTypeApprovalRequirementId);
        builder.Property(r => r.RequestTypeApprovalRequirementId).ValueGeneratedOnAdd();

        builder.Property(r => r.ApprovalType).HasConversion<byte>().IsRequired();
        builder.Property(r => r.TargetKind).HasConversion<byte>().IsRequired();
        builder.Property(r => r.TargetRoleName).HasMaxLength(64);
        builder.Property(r => r.BlocksWorkUntilApproved).IsRequired();
        builder.Property(r => r.IsActive).IsRequired();

        builder.HasIndex(r => new { r.RequestTypeId, r.ApprovalType }).IsUnique();

        builder.HasOne<RequestType>()
            .WithMany()
            .HasForeignKey(r => r.RequestTypeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(r => r.TargetDepartmentId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(r => r.TargetEmployeeId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

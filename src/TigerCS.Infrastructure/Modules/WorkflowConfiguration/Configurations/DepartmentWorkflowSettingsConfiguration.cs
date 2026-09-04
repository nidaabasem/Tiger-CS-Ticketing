using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Configurations;

/// <summary>Workflow/SLA Configuration phase 1 — at most one workflow-settings row per existing Department (identifying 1:1, mirroring SlaPolicy↔Priority).</summary>
public class DepartmentWorkflowSettingsConfiguration : IEntityTypeConfiguration<DepartmentWorkflowSettings>
{
    public void Configure(EntityTypeBuilder<DepartmentWorkflowSettings> builder)
    {
        builder.ToTable("DepartmentWorkflowSettings");

        builder.HasKey(s => s.DepartmentId);
        builder.Property(s => s.DepartmentId).ValueGeneratedNever();

        builder.Property(s => s.AllowAssignment).IsRequired();
        builder.Property(s => s.AllowInternalReassignment).IsRequired();
        builder.Property(s => s.AllowTransferToOtherDepartments).IsRequired();
        builder.Property(s => s.HeadRoleName).HasMaxLength(64).IsRequired();
        builder.Property(s => s.SupervisorRoleName).HasMaxLength(64).IsRequired();

        builder.HasOne<Department>()
            .WithOne()
            .HasForeignKey<DepartmentWorkflowSettings>(s => s.DepartmentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Configurations;

public class WorkflowTemplateStepConfiguration : IEntityTypeConfiguration<WorkflowTemplateStep>
{
    public void Configure(EntityTypeBuilder<WorkflowTemplateStep> builder)
    {
        builder.ToTable("WorkflowTemplateSteps");

        builder.HasKey(s => s.WorkflowTemplateStepId);
        builder.Property(s => s.WorkflowTemplateStepId).ValueGeneratedOnAdd();

        builder.Property(s => s.Sequence).IsRequired();
        builder.Property(s => s.Name).HasMaxLength(100).IsRequired();
        builder.Property(s => s.Kind).HasConversion<byte>().IsRequired();
        builder.Property(s => s.IsOptional).IsRequired();
        builder.Property(s => s.ApprovalType).HasConversion<byte?>();

        // The department a Department Queue / Assignment step routes to (a
        // handoff or a return to Customer Service). Restrict: a department is
        // never deleted while configuration still names it.
        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(s => s.DepartmentId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // Not unique any more: the designer reorders a Draft by swapping two
        // steps' sequences in one save, which a unique index would reject
        // mid-batch. Uniqueness of positions is enforced by the aggregate
        // (renumbering) and re-checked by publish validation.
        builder.HasIndex(s => new { s.WorkflowTemplateId, s.Sequence });

        builder.Navigation(s => s.Transitions)
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .AutoInclude();
    }
}

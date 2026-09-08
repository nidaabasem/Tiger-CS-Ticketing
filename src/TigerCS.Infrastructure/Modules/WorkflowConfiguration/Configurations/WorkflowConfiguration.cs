using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Configurations;

public class WorkflowConfiguration : IEntityTypeConfiguration<Workflow>
{
    public void Configure(EntityTypeBuilder<Workflow> builder)
    {
        builder.ToTable("Workflows");

        builder.HasKey(w => w.WorkflowId);
        builder.Property(w => w.WorkflowId).ValueGeneratedOnAdd();

        builder.Property(w => w.Code).HasMaxLength(Workflow.CodeMaxLength).IsRequired();
        builder.HasIndex(w => w.Code).IsUnique();

        builder.Property(w => w.Name).HasMaxLength(100).IsRequired();
        builder.Property(w => w.Description).HasMaxLength(500);
        builder.Property(w => w.IsActive).IsRequired();
        builder.Property(w => w.CreatedAtUtc).IsRequired();
    }
}

public class WorkflowStepTransitionConfiguration : IEntityTypeConfiguration<WorkflowStepTransition>
{
    public void Configure(EntityTypeBuilder<WorkflowStepTransition> builder)
    {
        builder.ToTable("WorkflowStepTransitions");

        builder.HasKey(t => t.WorkflowStepTransitionId);
        builder.Property(t => t.WorkflowStepTransitionId).ValueGeneratedOnAdd();

        builder.Property(t => t.Outcome).HasConversion<byte>().IsRequired();

        builder.HasIndex(t => new { t.WorkflowTemplateStepId, t.Outcome }).IsUnique();

        builder.HasOne(t => t.Step)
            .WithMany(s => s.Transitions)
            .HasForeignKey(t => t.WorkflowTemplateStepId)
            .OnDelete(DeleteBehavior.Cascade);

        // The target lives in the same version; the aggregate drops every
        // branch pointing at a step before that step is removed, so Restrict
        // only ever guards against out-of-band deletes.
        builder.HasOne(t => t.TargetStep)
            .WithMany()
            .HasForeignKey(t => t.TargetWorkflowTemplateStepId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

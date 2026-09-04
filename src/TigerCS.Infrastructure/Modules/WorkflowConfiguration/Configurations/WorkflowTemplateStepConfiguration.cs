using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Configurations;

/// <summary>Workflow/SLA Configuration phase 1 — the displayable, ordered steps of a template's flow.</summary>
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

        // One sequence number per template — the stored order IS the display
        // order, and can never be ambiguous.
        builder.HasIndex(s => new { s.WorkflowTemplateId, s.Sequence }).IsUnique();
    }
}

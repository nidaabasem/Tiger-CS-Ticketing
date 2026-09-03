using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Configurations;

/// <summary>Workflow/SLA Configuration phase 1 — the reusable workflow patterns (Standard / With Pending / With Approval).</summary>
public class WorkflowTemplateConfiguration : IEntityTypeConfiguration<WorkflowTemplate>
{
    public void Configure(EntityTypeBuilder<WorkflowTemplate> builder)
    {
        builder.ToTable("WorkflowTemplates");

        builder.HasKey(t => t.WorkflowTemplateId);
        builder.Property(t => t.WorkflowTemplateId).ValueGeneratedOnAdd();

        builder.Property(t => t.Code).HasMaxLength(32).IsRequired();
        builder.HasIndex(t => t.Code).IsUnique();

        builder.Property(t => t.Name).HasMaxLength(100).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(500);
        builder.Property(t => t.AllowsPendingCustomer).IsRequired();
        builder.Property(t => t.AllowsPendingInternal).IsRequired();
        builder.Property(t => t.RequiresApproval).IsRequired();
        builder.Property(t => t.IsActive).IsRequired();

        builder.HasMany(t => t.Steps)
            .WithOne(s => s.WorkflowTemplate!)
            .HasForeignKey(s => s.WorkflowTemplateId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(t => t.Steps)
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .AutoInclude();
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Configurations;

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

        builder.Property(t => t.WorkflowId).IsRequired();
        builder.Property(t => t.VersionNumber).IsRequired();
        builder.Property(t => t.Status).HasConversion<byte>().IsRequired();
        builder.Property(t => t.CreatedAtUtc).IsRequired();

        builder.HasIndex(t => new { t.WorkflowId, t.VersionNumber }).IsUnique();

        // At most one Published and at most one Draft per workflow — the
        // database-level guarantee behind "the active version".
        // Two named indexes on the same column: naming them at HasIndex is
        // what makes EF keep both (an unnamed second HasIndex on the same
        // properties would replace the first).
        builder.HasIndex(t => t.WorkflowId, "UX_WorkflowTemplates_OnePublishedPerWorkflow")
            .IsUnique()
            .HasFilter("[Status] = 2");

        builder.HasIndex(t => t.WorkflowId, "UX_WorkflowTemplates_OneDraftPerWorkflow")
            .IsUnique()
            .HasFilter("[Status] = 1");

        builder.HasOne<Workflow>()
            .WithMany()
            .HasForeignKey(t => t.WorkflowId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(t => t.CreatedByEmployeeId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(t => t.PublishedByEmployeeId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(t => t.Steps)
            .WithOne(s => s.WorkflowTemplate!)
            .HasForeignKey(s => s.WorkflowTemplateId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(t => t.Steps)
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .AutoInclude();
    }
}

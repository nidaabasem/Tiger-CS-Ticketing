using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Configurations;

/// <summary>Workflow/SLA Configuration phase 1 — the per-department request type configuration.</summary>
public class RequestTypeConfiguration : IEntityTypeConfiguration<RequestType>
{
    public void Configure(EntityTypeBuilder<RequestType> builder)
    {
        builder.ToTable("RequestTypes");

        builder.HasKey(r => r.RequestTypeId);
        builder.Property(r => r.RequestTypeId).ValueGeneratedOnAdd();

        builder.Property(r => r.Name).HasMaxLength(100).IsRequired();
        builder.Property(r => r.DefaultPriorityId).IsRequired();
        builder.Property(r => r.RequiredFieldsJson).HasMaxLength(2000);
        builder.Property(r => r.IsActive).IsRequired();

        // "Ticketing System" and "E-mail" legitimately exist under both
        // Customer Service and Collections — uniqueness is per department,
        // not global.
        builder.HasIndex(r => new { r.DepartmentId, r.Name }).IsUnique();

        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(r => r.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<WorkflowTemplate>()
            .WithMany()
            .HasForeignKey(r => r.WorkflowTemplateId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Priority>()
            .WithMany()
            .HasForeignKey(r => r.DefaultPriorityId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

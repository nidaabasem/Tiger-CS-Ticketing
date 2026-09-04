using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Configurations;

/// <summary>Workflow/SLA Configuration phase 1 — the per-(request type, priority) SLA configuration, ranges and triggers included.</summary>
public class RequestTypeSlaPolicyConfiguration : IEntityTypeConfiguration<RequestTypeSlaPolicy>
{
    public void Configure(EntityTypeBuilder<RequestTypeSlaPolicy> builder)
    {
        builder.ToTable("RequestTypeSlaPolicies");

        builder.HasKey(p => p.RequestTypeSlaPolicyId);
        builder.Property(p => p.RequestTypeSlaPolicyId).ValueGeneratedOnAdd();

        builder.Property(p => p.PriorityId).IsRequired();
        builder.Property(p => p.Trigger).HasConversion<byte>().IsRequired();
        builder.Property(p => p.Unit).HasConversion<byte>().IsRequired();
        builder.Property(p => p.IsImmediate).IsRequired();
        builder.Property(p => p.ClockBasis).HasConversion<byte?>();
        builder.Property(p => p.WarningThresholdPercent).HasPrecision(5, 2);
        builder.Property(p => p.IsActive).IsRequired();

        // One SLA row per (request type, priority) — Normal vs. Urgent are
        // rows of the same request type at different priorities, never two
        // request types.
        builder.HasIndex(p => new { p.RequestTypeId, p.PriorityId }).IsUnique();

        builder.HasOne<RequestType>()
            .WithMany()
            .HasForeignKey(p => p.RequestTypeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Priority>()
            .WithMany()
            .HasForeignKey(p => p.PriorityId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

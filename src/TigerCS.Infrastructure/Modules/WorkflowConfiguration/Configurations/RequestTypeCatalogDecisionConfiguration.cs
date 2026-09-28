using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Configurations;

/// <summary>The request-type catalog import's open business questions, and their recorded answers.</summary>
public class RequestTypeCatalogDecisionConfiguration : IEntityTypeConfiguration<RequestTypeCatalogDecision>
{
    public void Configure(EntityTypeBuilder<RequestTypeCatalogDecision> builder)
    {
        builder.ToTable("RequestTypeCatalogDecisions");

        builder.HasKey(d => d.RequestTypeCatalogDecisionId);
        builder.Property(d => d.RequestTypeCatalogDecisionId).ValueGeneratedOnAdd();

        builder.Property(d => d.Area).HasMaxLength(RequestTypeCatalogDecision.AreaMaxLength).IsRequired();
        builder.Property(d => d.Question).HasMaxLength(RequestTypeCatalogDecision.TextMaxLength).IsRequired();
        builder.Property(d => d.Resolution).HasMaxLength(RequestTypeCatalogDecision.TextMaxLength);
        builder.Property(d => d.CreatedAtUtc).IsRequired();
        builder.Ignore(d => d.IsResolved);

        builder.HasIndex(d => d.RequestTypeId);

        builder.HasOne<RequestType>()
            .WithMany()
            .HasForeignKey(d => d.RequestTypeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

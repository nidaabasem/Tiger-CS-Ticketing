using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Configurations;

/// <summary>Workflow/Automation phase 2 — the per-request-type assignment rule (mode + primary + team members). References existing employees only.</summary>
public class RequestTypeAssignmentRuleConfiguration : IEntityTypeConfiguration<RequestTypeAssignmentRule>
{
    public void Configure(EntityTypeBuilder<RequestTypeAssignmentRule> builder)
    {
        builder.ToTable("RequestTypeAssignmentRules");

        builder.HasKey(r => r.RequestTypeAssignmentRuleId);
        builder.Property(r => r.RequestTypeAssignmentRuleId).ValueGeneratedOnAdd();

        builder.Property(r => r.Mode).HasConversion<byte>().IsRequired();
        builder.Property(r => r.TeamName).HasMaxLength(100);
        builder.Property(r => r.IsActive).IsRequired();

        // At most one rule per request type — the automation resolves the
        // rule, not a list of competing rules.
        builder.HasIndex(r => r.RequestTypeId).IsUnique();

        builder.HasOne<RequestType>()
            .WithOne()
            .HasForeignKey<RequestTypeAssignmentRule>(r => r.RequestTypeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(r => r.PrimaryEmployeeId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(r => r.Members)
            .WithOne(m => m.Rule!)
            .HasForeignKey(m => m.RequestTypeAssignmentRuleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(r => r.Members)
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .AutoInclude();
    }
}

/// <summary>One non-primary team member of an assignment rule.</summary>
public class RequestTypeAssignmentRuleMemberConfiguration : IEntityTypeConfiguration<RequestTypeAssignmentRuleMember>
{
    public void Configure(EntityTypeBuilder<RequestTypeAssignmentRuleMember> builder)
    {
        builder.ToTable("RequestTypeAssignmentRuleMembers");

        builder.HasKey(m => m.RequestTypeAssignmentRuleMemberId);
        builder.Property(m => m.RequestTypeAssignmentRuleMemberId).ValueGeneratedOnAdd();

        // The same employee appears at most once per rule.
        builder.HasIndex(m => new { m.RequestTypeAssignmentRuleId, m.EmployeeId }).IsUnique();

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(m => m.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

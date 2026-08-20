using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Audit;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Audit;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.CustomerVerification.Configurations;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Configurations;

namespace TigerCS.Infrastructure.Persistence;

/// <summary>
/// This increment adds Customer Verification — Tiger CS Ticketing's own
/// business logic for verifying a requester against Tiger CRM's read-only
/// data (ADR-0006/0007, MVP-Data-Dictionary.md §2.7/§2.24) — and a minimal
/// cross-cutting audit trail (§2.22) on top of Identity and Access
/// (ADR-0004/0005). Ticketing/SLA/Genesys tables are not mapped here yet —
/// those arrive with their own modules' increments.
/// </summary>
public class TigerCsDbContext(DbContextOptions<TigerCsDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    public DbSet<Employee> Employees => Set<Employee>();

    public DbSet<Department> Departments => Set<Department>();

    public DbSet<UserDepartmentAssignment> UserDepartmentAssignments => Set<UserDepartmentAssignment>();

    public DbSet<UnitReference> UnitReferences => Set<UnitReference>();

    public DbSet<ContactReference> ContactReferences => Set<ContactReference>();

    public DbSet<VerificationSession> VerificationSessions => Set<VerificationSession>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new ApplicationRoleConfiguration());
        builder.ApplyConfiguration(new EmployeeConfiguration());
        builder.ApplyConfiguration(new DepartmentConfiguration());
        builder.ApplyConfiguration(new UserDepartmentAssignmentConfiguration());

        builder.ApplyConfiguration(new UnitReferenceConfiguration());
        builder.ApplyConfiguration(new ContactReferenceConfiguration());
        builder.ApplyConfiguration(new VerificationSessionConfiguration());

        builder.ApplyConfiguration(new AuditEntryConfiguration());
    }
}

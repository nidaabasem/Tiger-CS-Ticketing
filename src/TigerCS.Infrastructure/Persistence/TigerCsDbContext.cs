using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Configurations;

namespace TigerCS.Infrastructure.Persistence;

/// <summary>
/// This increment's scope is Identity and Access only (ADR-0004/0005):
/// Identity's own tables, Employees, Departments, and
/// UserDepartmentAssignments. No ticketing/SLA/Genesys tables are mapped
/// here yet — those arrive with their own modules' increments.
/// </summary>
public class TigerCsDbContext(DbContextOptions<TigerCsDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    public DbSet<Employee> Employees => Set<Employee>();

    public DbSet<Department> Departments => Set<Department>();

    public DbSet<UserDepartmentAssignment> UserDepartmentAssignments => Set<UserDepartmentAssignment>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new ApplicationRoleConfiguration());
        builder.ApplyConfiguration(new EmployeeConfiguration());
        builder.ApplyConfiguration(new DepartmentConfiguration());
        builder.ApplyConfiguration(new UserDepartmentAssignmentConfiguration());
    }
}

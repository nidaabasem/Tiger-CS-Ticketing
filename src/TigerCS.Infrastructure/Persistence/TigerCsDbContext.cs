using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Audit;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Audit;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.ClassificationAndRouting.Configurations;
using TigerCS.Infrastructure.Modules.CustomerVerification.Configurations;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Configurations;
using TigerCS.Infrastructure.Modules.SlaAndEscalation.Configurations;
using TigerCS.Infrastructure.Modules.Ticketing.Configurations;

namespace TigerCS.Infrastructure.Persistence;

/// <summary>
/// This increment adds Ticket Intake and Ticket Creation — IntakeRecords,
/// Tickets, TicketRequesterSnapshots, TicketStatusHistory (Ticketing
/// module), plus the minimal Categories/Priorities reference data Ticketing
/// needs to classify, prioritize, and route a ticket at creation
/// (Classification and Routing / SLA and Escalation module ownership per
/// Module-Design.md — only the reference data those modules own is mapped
/// here, not their own business logic, which remains out of scope). SLA due-
/// date computation, escalation, Genesys, notifications, and attachments are
/// not mapped here yet — those arrive with their own modules' increments.
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

    public DbSet<Priority> Priorities => Set<Priority>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<IntakeRecord> IntakeRecords => Set<IntakeRecord>();

    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<TicketRequesterSnapshot> TicketRequesterSnapshots => Set<TicketRequesterSnapshot>();

    public DbSet<TicketStatusHistory> TicketStatusHistoryEntries => Set<TicketStatusHistory>();

    public DbSet<TicketAssignment> TicketAssignments => Set<TicketAssignment>();

    public DbSet<TicketResolution> TicketResolutions => Set<TicketResolution>();

    public DbSet<TicketNote> TicketNotes => Set<TicketNote>();

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

        builder.ApplyConfiguration(new PriorityConfiguration());
        builder.ApplyConfiguration(new CategoryConfiguration());
        builder.ApplyConfiguration(new IntakeRecordConfiguration());
        builder.ApplyConfiguration(new TicketConfiguration());
        builder.ApplyConfiguration(new TicketRequesterSnapshotConfiguration());
        builder.ApplyConfiguration(new TicketStatusHistoryConfiguration());
        builder.ApplyConfiguration(new TicketAssignmentConfiguration());
        builder.ApplyConfiguration(new TicketResolutionConfiguration());
        builder.ApplyConfiguration(new TicketNoteConfiguration());

        // Supplemental — see this configuration's own remarks for why it is
        // not folded into VerificationSessionConfiguration (a
        // CustomerVerification-module file this increment does not modify).
        builder.ApplyConfiguration(new VerificationSessionConsumptionConcurrencyConfiguration());
    }
}

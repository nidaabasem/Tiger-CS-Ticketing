using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Domain.Audit;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.SlaAndEscalation.Seed;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Tests.IdentityAndAccess.Integration;

/// <summary>
/// Hosts the real Api in-process for 401/403 smoke tests, with the SQL
/// Server-backed DbContext swapped for a per-instance EF Core InMemory
/// database — no real SQL Server is needed to run this test class.
/// </summary>
public sealed class TigerCsApiFactory : WebApplicationFactory<Program>
{
    public readonly string DatabaseName = Guid.NewGuid().ToString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Explicit, deterministic environment name — distinct from "Development"
        // (so DevSeedData's own auto-seeding doesn't also run and race with this
        // factory's manual seeding) and from "Production" (Program.cs now
        // refuses to start at all in Production, per review item 8).
        builder.UseEnvironment("Testing");

        // ADR-0015's Hangfire server provisions its own SQL Server schema on
        // startup, which this InMemory-backed host has no database for.
        //
        // Set through UseSetting rather than ConfigureAppConfiguration
        // because the background-job registration reads this value while
        // services are being registered, and ConfigureAppConfiguration's
        // sources are not merged into builder.Configuration until the host is
        // built — the same eager-versus-lazy ordering AddTigerCsInfrastructure
        // already documents for its own connection-string read. UseSetting
        // writes into builder.Configuration immediately, exactly as
        // UseEnvironment above does.
        //
        // Breach detection itself stays fully wired:
        // SlaBreachDetectionAppService is registered and exercised unchanged,
        // and the scheduler becomes NoOpSlaDeadlineScheduler. This governs job
        // execution, never job logic.
        builder.UseSetting("BackgroundJobs:Enabled", "false");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TigerCsDatabase"] = "Server=(unused-for-tests);Database=(unused-for-tests);",
                ["Jwt:Issuer"] = "TigerCS.Tests",
                ["Jwt:Audience"] = "TigerCS.Tests.Client",
                ["Jwt:SigningKey"] = "test-only-signing-key-at-least-32-characters-long-1234567890",
                ["Jwt:ExpirationMinutes"] = "60"
            });
        });

        builder.ConfigureServices(services =>
        {
            // Program.cs registers TigerCsDbContext against SqlServer. Swapping in
            // the InMemory provider for tests requires removing every descriptor
            // that registration added — EF Core refuses to build its internal
            // service provider if two database providers are both registered,
            // so trimming only the DbContextOptions<T> entry is not enough.
            var efCoreDescriptors = services
                .Where(d => (d.ServiceType.FullName ?? string.Empty).Contains("EntityFrameworkCore", StringComparison.Ordinal)
                    || (d.ServiceType == typeof(TigerCsDbContext))
                    || (d.ServiceType == typeof(DbContextOptions<TigerCsDbContext>))
                    || (d.ServiceType == typeof(DbContextOptions)))
                .ToList();
            foreach (var descriptor in efCoreDescriptors)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<TigerCsDbContext>(options => options
                .UseInMemoryDatabase(DatabaseName)
                // The InMemory provider does not support real transactions
                // (TicketingUnitOfWork.BeginTransactionAsync, added by the
                // senior review's atomicity fix — item 11) and logs
                // TransactionIgnoredWarning, which EF Core escalates to an
                // exception by default. Real SQL Server (Program.cs's own
                // registration, untouched here) does support transactions
                // and never raises this warning — this suppression is
                // test-only, matching the same InMemory-vs-real-SQL-Server
                // testing split already established for unique-index
                // enforcement elsewhere in this codebase (see
                // CustomerVerificationUnitOfWork's remarks).
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        });
    }

    /// <summary>Seeds roles + one employee with the given role, returning (username, password, employeeId).</summary>
    public async Task<(string Username, string Password, Guid EmployeeId)> SeedEmployeeAsync(string roleName)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        await db.Database.EnsureCreatedAsync();

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        foreach (var roleToSeed in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(roleToSeed))
            {
                await roleManager.CreateAsync(new ApplicationRole(roleToSeed, roleToSeed));
            }
        }

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var username = $"user-{Guid.NewGuid():N}";
        const string password = "Test-Password-1!";
        var user = new ApplicationUser { UserName = username, Email = username };
        var createResult = await userManager.CreateAsync(user, password);
        if (!createResult.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", createResult.Errors.Select(e => e.Description)));
        }

        await userManager.AddToRoleAsync(user, roleName);

        db.Employees.Add(new Employee(user.Id, "Test Employee", isGeynessStaff: false, DateTime.UtcNow));
        await db.SaveChangesAsync();

        return (username, password, user.Id);
    }

    /// <summary>Creates a department directly (bypassing the app services — this is test setup, not the thing under test).</summary>
    public async Task<int> CreateDepartmentAsync(string name, string code)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var department = new Department(name, code);
        db.Departments.Add(department);
        await db.SaveChangesAsync();
        return department.DepartmentId;
    }

    /// <summary>Assigns an employee to a department (primary), replacing any existing primary assignment.</summary>
    public async Task AssignPrimaryDepartmentAsync(Guid employeeId, int departmentId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();

        var existingPrimary = await db.UserDepartmentAssignments
            .FirstOrDefaultAsync(a => a.EmployeeId == employeeId && a.IsPrimary);
        existingPrimary?.ClearPrimary();

        db.UserDepartmentAssignments.Add(new UserDepartmentAssignment(
            employeeId, departmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        await db.SaveChangesAsync();
    }

    /// <summary>Creates a category routed to the given department (bypassing the app services — test setup, not the thing under test).</summary>
    public async Task<int> CreateCategoryAsync(string name, int departmentId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var category = new Category(name, departmentId);
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return category.CategoryId;
    }

    /// <summary>
    /// Seeds the fixed MVP priority set and its SLA reference data
    /// (idempotent), needed since this factory does not run DevSeedData.
    ///
    /// <para>
    /// The SLA policies and business calendar come from
    /// <see cref="SlaReferenceData"/> — the same source a real deployment
    /// seeds from — rather than being restated here, so an endpoint test can
    /// never pass against target values that differ from the approved ones.
    /// </para>
    /// </summary>
    public async Task SeedPrioritiesAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();

        if (!await db.Priorities.AnyAsync())
        {
            foreach (var level in Enum.GetValues<PriorityLevel>())
            {
                db.Priorities.Add(new Priority((byte)level, level.ToString(), (byte)level));
            }

            await db.SaveChangesAsync();
        }

        await SlaReferenceData.SeedAsync(db);
    }

    /// <summary>Reads a ticket's current SLA period directly, for assertions that need the stored due dates rather than the API projection.</summary>
    public async Task<TicketSlaInstance?> GetCurrentSlaInstanceAsync(long ticketId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        return await db.TicketSlaInstances.FirstOrDefaultAsync(i => i.TicketId == ticketId && i.PeriodEndAtUtc == null);
    }

    /// <summary>Rewrites a ticket's current SLA due timestamps, so a test can put a deadline in the past without waiting for one.</summary>
    public async Task ForceSlaDueDatesAsync(long ticketId, DateTime firstResponseDueAtUtc, DateTime resolutionDueAtUtc)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();

        var instance = await db.TicketSlaInstances.FirstAsync(i => i.TicketId == ticketId && i.PeriodEndAtUtc == null);

        // Test setup reaching past the domain's own write-once surface on
        // purpose: TicketSlaInstance exposes no due-date setter, which is
        // exactly the property under test everywhere else.
        db.Entry(instance).Property(nameof(TicketSlaInstance.FirstResponseDueAtUtc)).CurrentValue = firstResponseDueAtUtc;
        db.Entry(instance).Property(nameof(TicketSlaInstance.ResolutionDueAtUtc)).CurrentValue = resolutionDueAtUtc;

        await db.SaveChangesAsync();
    }

    /// <summary>Every escalation recorded against a ticket, newest first.</summary>
    public async Task<IReadOnlyList<TicketEscalation>> GetEscalationsAsync(long ticketId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        return await db.TicketEscalations
            .Where(e => e.TicketId == ticketId)
            .OrderByDescending(e => e.TicketEscalationId)
            .ToListAsync();
    }

    /// <summary>Audit entries recorded against a ticket, for the audit assertions.</summary>
    public async Task<IReadOnlyList<AuditEntry>> GetAuditEntriesAsync(string entityId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        return await db.AuditEntries.Where(a => a.EntityId == entityId).ToListAsync();
    }

    /// <summary>Runs a breach check exactly as a fired scheduled job or a sweep tick would, without a scheduler present.</summary>
    public async Task<SlaBreachProcessingOutcome> RunDeadlineCheckAsync(long ticketId, SlaDeadlineType deadlineType)
    {
        using var scope = Services.CreateScope();
        var detection = scope.ServiceProvider.GetRequiredService<SlaBreachDetectionAppService>();
        return await detection.CheckDeadlineAsync(ticketId, deadlineType);
    }

    /// <summary>Runs SLA-Architecture.md §14's safety sweep against the whole host.</summary>
    public async Task<SlaSweepResult> RunSlaSweepAsync()
    {
        using var scope = Services.CreateScope();
        var detection = scope.ServiceProvider.GetRequiredService<SlaBreachDetectionAppService>();
        return await detection.SweepAsync();
    }
}

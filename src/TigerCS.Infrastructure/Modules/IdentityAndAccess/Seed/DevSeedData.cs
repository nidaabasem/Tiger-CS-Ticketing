using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.SlaAndEscalation.Seed;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Seed;

/// <summary>
/// Development-only seed: roles, sample departments, and an optional
/// development administrator. Never runs against anything but a local
/// development database (see Program.cs's environment check) and never
/// contains a real password — the admin's password is read from
/// user-secrets/environment variables (see docs/DEV-SETUP.md); if absent,
/// no administrator is created and the seed proceeds with roles/departments
/// only.
/// </summary>
public static class DevSeedData
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("DevSeedData");
        var dbContext = services.GetRequiredService<TigerCsDbContext>();
        var roleManager = services.GetRequiredService<RoleManager<ApplicationRole>>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var configuration = services.GetRequiredService<IConfiguration>();

        await SeedRolesAsync(roleManager, logger, cancellationToken);
        await SeedDepartmentsAsync(dbContext, logger, cancellationToken);
        await SeedPrioritiesAsync(dbContext, logger, cancellationToken);
        await SeedCategoriesAsync(dbContext, logger, cancellationToken);
        await SeedDepartmentCustomerLookupSourcesAsync(dbContext, logger, cancellationToken);
        await SeedSlaReferenceDataAsync(dbContext, logger, cancellationToken);
        await SeedDevAdministratorAsync(dbContext, userManager, configuration, logger, cancellationToken);
    }

    private static async Task SeedPrioritiesAsync(TigerCsDbContext dbContext, ILogger logger, CancellationToken cancellationToken)
    {
        if (await dbContext.Priorities.AnyAsync(cancellationToken))
        {
            return;
        }

        // The fixed MVP set (MVP-Data-Dictionary.md §2.6). SlaPolicies, its
        // identifying 1:1 partner, is seeded by SeedSlaReferenceDataAsync
        // below — it must exist wherever these rows do, since a ticket
        // cannot get a due date without it.
        (PriorityLevel Level, string Name)[] priorities =
        [
            (PriorityLevel.Critical, "Critical"),
            (PriorityLevel.High, "High"),
            (PriorityLevel.Medium, "Medium"),
            (PriorityLevel.Low, "Low")
        ];

        foreach (var (level, name) in priorities)
        {
            dbContext.Priorities.Add(new Priority((byte)level, name, (byte)level));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeded {Count} priorities.", priorities.Length);
    }

    /// <summary>
    /// The SLA policies and default business calendar (backlog S-04's
    /// Definition of Done). Delegates to <see cref="SlaReferenceData"/> so
    /// the approved target values live in exactly one place and cannot drift
    /// between what a developer runs and what the integration tests assert.
    /// </summary>
    private static async Task SeedSlaReferenceDataAsync(TigerCsDbContext dbContext, ILogger logger, CancellationToken cancellationToken)
    {
        var alreadySeeded = await dbContext.SlaPolicies.AnyAsync(cancellationToken)
            && await dbContext.BusinessCalendars.AnyAsync(cancellationToken);

        if (alreadySeeded)
        {
            return;
        }

        await SlaReferenceData.SeedAsync(dbContext, cancellationToken);
        logger.LogInformation("Seeded SLA policies and the default business calendar (ISSUE-017 Option A: Sat-Thu, 08:00-18:00).");
    }

    private static async Task SeedCategoriesAsync(TigerCsDbContext dbContext, ILogger logger, CancellationToken cancellationToken)
    {
        if (await dbContext.Categories.AnyAsync(cancellationToken))
        {
            return;
        }

        var customerService = await dbContext.Departments.FirstOrDefaultAsync(d => d.Code == "CS", cancellationToken);
        var facilities = await dbContext.Departments.FirstOrDefaultAsync(d => d.Code == "FM", cancellationToken);
        if (customerService is null || facilities is null)
        {
            logger.LogWarning("Skipping category seed — expected departments (CS, FM) not found.");
            return;
        }

        dbContext.Categories.Add(new Category("General Inquiry", customerService.DepartmentId));
        dbContext.Categories.Add(new Category("Corrective Maintenance", facilities.DepartmentId));

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeded 2 sample categories.");
    }

    /// <summary>
    /// Sample Department → customer-lookup-source mapping (business-rule
    /// change: customer lookup narrows to a Department's configured
    /// source(s) instead of always searching CRM+PACT+Tasleeh). Illustrative
    /// dev-only data, not a business decision — Customer Service maps to
    /// CRM only, Facilities Management to CRM+Tasleeh, so both a
    /// single-source and a multi-source Department exist to exercise
    /// against locally.
    /// </summary>
    private static async Task SeedDepartmentCustomerLookupSourcesAsync(TigerCsDbContext dbContext, ILogger logger, CancellationToken cancellationToken)
    {
        if (await dbContext.DepartmentCustomerLookupSources.AnyAsync(cancellationToken))
        {
            return;
        }

        var customerService = await dbContext.Departments.FirstOrDefaultAsync(d => d.Code == "CS", cancellationToken);
        var facilities = await dbContext.Departments.FirstOrDefaultAsync(d => d.Code == "FM", cancellationToken);
        if (customerService is null || facilities is null)
        {
            logger.LogWarning("Skipping department customer-lookup-source seed — expected departments (CS, FM) not found.");
            return;
        }

        dbContext.DepartmentCustomerLookupSources.Add(
            new DepartmentCustomerLookupSource(customerService.DepartmentId, CustomerLookupSource.Crm));
        dbContext.DepartmentCustomerLookupSources.Add(
            new DepartmentCustomerLookupSource(facilities.DepartmentId, CustomerLookupSource.Crm));
        dbContext.DepartmentCustomerLookupSources.Add(
            new DepartmentCustomerLookupSource(facilities.DepartmentId, CustomerLookupSource.Tasleeh));

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeded department customer-lookup-source mappings (CS -> Crm; FM -> Crm+Tasleeh).");
    }

    private static async Task SeedRolesAsync(
        RoleManager<ApplicationRole> roleManager, ILogger logger, CancellationToken cancellationToken)
    {
        foreach (var roleName in Roles.All)
        {
            if (await roleManager.RoleExistsAsync(roleName))
            {
                continue;
            }

            var description = Roles.Descriptions.TryGetValue(roleName, out var d) ? d : string.Empty;
            var result = await roleManager.CreateAsync(new ApplicationRole(roleName, description));
            if (!result.Succeeded)
            {
                logger.LogWarning("Failed to seed role {RoleName}: {Errors}",
                    roleName, string.Join("; ", result.Errors.Select(e => e.Description)));
            }
        }
    }

    private static async Task SeedDepartmentsAsync(TigerCsDbContext dbContext, ILogger logger, CancellationToken cancellationToken)
    {
        if (await dbContext.Departments.AnyAsync(cancellationToken))
        {
            return;
        }

        string[][] sampleDepartments =
        [
            ["Customer Service", "CS"],
            ["Facilities Management", "FM"],
            ["Finance", "FIN"],
            ["Information Technology", "IT"]
        ];

        foreach (var d in sampleDepartments)
        {
            dbContext.Departments.Add(new Department(d[0], d[1]));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeded {Count} sample departments.", sampleDepartments.Length);
    }

    private static async Task SeedDevAdministratorAsync(
        TigerCsDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var username = configuration["DevAdmin:Username"];
        var password = configuration["DevAdmin:Password"];
        var displayName = configuration["DevAdmin:DisplayName"] ?? "Development Administrator";

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogInformation(
                "DevAdmin:Username/DevAdmin:Password not set — skipping development administrator seed. " +
                "See docs/DEV-SETUP.md to create one via user-secrets or environment variables.");
            return;
        }

        if (await userManager.FindByNameAsync(username) is not null)
        {
            return;
        }

        var user = new ApplicationUser { UserName = username, Email = username };
        var createResult = await userManager.CreateAsync(user, password);
        if (!createResult.Succeeded)
        {
            logger.LogWarning("Failed to create development administrator: {Errors}",
                string.Join("; ", createResult.Errors.Select(e => e.Description)));
            return;
        }

        await userManager.AddToRoleAsync(user, Roles.SystemAdministrator);

        dbContext.Employees.Add(new Employee(user.Id, displayName, isGeynessStaff: false, DateTime.UtcNow));
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Development administrator '{Username}' created.", username);
    }
}

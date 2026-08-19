using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Identity;
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
        await SeedDevAdministratorAsync(dbContext, userManager, configuration, logger, cancellationToken);
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

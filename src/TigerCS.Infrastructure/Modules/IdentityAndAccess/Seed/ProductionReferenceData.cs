
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.SlaAndEscalation.Seed;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;
using TigerCS.Infrastructure.Persistence;
namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Seed;

public static class ProductionReferenceData
{
    private static readonly (string Name, string Code)[] Departments =
    [
        ("Customer Service", "CS"),
        ("Collections", "COL"),
        ("Registration", "REG"),
        ("Handover", "HO"),
        ("Call Center", "CC"),
        ("Accounting", "ACC"),
        ("Leasing Customer Services", "LCS"),
        ("Facilities Management", "FM")
    ];

    // Explicit invocation only. Do not call from Program.cs.
    public static async Task SeedAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var db = services.GetRequiredService<TigerCsDbContext>();

        var roleManager =
            services.GetRequiredService<RoleManager<ApplicationRole>>();

        var logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ProductionReferenceData");

        await SeedRolesAsync(roleManager, cancellationToken);
        await SeedDepartmentsAsync(db, cancellationToken);


        await SeedPrioritiesAsync(db, cancellationToken);
        await SlaReferenceData.SeedAsync(db, cancellationToken);
        await WorkflowReferenceData.SeedAsync(db, cancellationToken);

        var expectedDepartments = new[]
{
    "CS", "COL", "REG", "HO",
    "CC", "ACC", "LCS", "FM"
};

        var existingCodes = await db.Departments
            .Select(d => d.Code)
            .ToListAsync(cancellationToken);

        var missing = expectedDepartments
            .Except(existingCodes, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "Missing production departments: " +
                string.Join(", ", missing));
        }



        var requestTypesResult = await NewRequestTypesImporter.ImportAsync(
    db, DateTime.UtcNow, cancellationToken);

        if (requestTypesResult.CreatedDepartments.Count > 0)
        {
            throw new InvalidOperationException(
                "Unexpected departments created during request type import: " +
                string.Join(", ", requestTypesResult.CreatedDepartments));
        }

        var leasingResult = await LeasingInquiryRequestTypeImporter.ImportAsync(
            db, DateTime.UtcNow, cancellationToken);

        if (leasingResult.Outcome == LeasingInquiryImportOutcome.DepartmentMissing ||
            leasingResult.Outcome == LeasingInquiryImportOutcome.SkippedCodeInUse)
        {
            throw new InvalidOperationException(
                $"Leasing Inquiry import requires review: {leasingResult.Outcome}");
        }

        logger.LogInformation(
      "Production reference-data seeding completed.");
    }

    private static async Task SeedRolesAsync(
        RoleManager<ApplicationRole> roleManager,
        CancellationToken cancellationToken)
    {
        foreach (var roleName in Roles.All)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await roleManager.RoleExistsAsync(roleName))
                continue;

            var description =
                Roles.Descriptions.TryGetValue(roleName, out var value)
                    ? value
                    : string.Empty;

            var result = await roleManager.CreateAsync(
                new ApplicationRole(roleName, description));

            if (!result.Succeeded)
            {
                var errors = string.Join("; ",
                    result.Errors.Select(e => e.Description));

                throw new InvalidOperationException(
                    $"Failed to create role {roleName}: {errors}");
            }
        }
    }

    private static async Task SeedDepartmentsAsync(
        TigerCsDbContext db,
        CancellationToken cancellationToken)
    {
        foreach (var (name, code) in Departments)
        {
            // Confirm the EF-mapped department property is "Code".
            var exists = await db.Departments.AnyAsync(d => d.Code == code, cancellationToken);
            if (exists)
                continue;

            db.Departments.Add(new Department(name, code));

            await db.SaveChangesAsync(cancellationToken);
        }
    }
    private static async Task SeedPrioritiesAsync(
    TigerCsDbContext db,
    CancellationToken cancellationToken)
    {
        (PriorityLevel Level, string Name)[] priorities =
        [
            (PriorityLevel.Critical, "Critical"),
        (PriorityLevel.High, "High"),
        (PriorityLevel.Medium, "Medium"),
        (PriorityLevel.Low, "Low")
        ];

        foreach (var (level, name) in priorities)
        {
            var id = (byte)level;

            if (await db.Priorities.AnyAsync(
                    p => p.PriorityId == id,
                    cancellationToken))
            {
                continue;
            }

            db.Priorities.Add(new Priority(id, name, id));
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

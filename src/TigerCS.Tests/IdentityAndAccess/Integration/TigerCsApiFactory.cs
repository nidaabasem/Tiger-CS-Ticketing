using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Identity;
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

            services.AddDbContext<TigerCsDbContext>(options => options.UseInMemoryDatabase(DatabaseName));
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
}

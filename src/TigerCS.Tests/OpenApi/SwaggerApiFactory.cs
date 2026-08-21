using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Tests.OpenApi;

/// <summary>
/// Hosts the real Api in-process under an arbitrary environment name, so the
/// Swagger surface can be probed in Development, Testing, and Production
/// without a real SQL Server. Only the DbContext provider is swapped — the
/// endpoint graph, authentication, authorization, and the OpenAPI wiring are
/// exactly what Program.cs builds.
/// </summary>
public sealed class SwaggerApiFactory(string environment) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);

        // ADR-0015's Hangfire server provisions its own SQL Server schema at
        // startup; these tests read the OpenAPI document from a host with no
        // database behind it. Set through UseSetting rather than
        // ConfigureAppConfiguration because the background-job registration
        // reads it during service registration, before that block's sources
        // are merged — see TigerCsApiFactory for the full note.
        builder.UseSetting("BackgroundJobs:Enabled", "false");

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:TigerCsDatabase"] = "Server=(unused-for-tests);Database=(unused-for-tests);",
            ["Jwt:Issuer"] = "TigerCS.Tests",
            ["Jwt:Audience"] = "TigerCS.Tests.Client",
            ["Jwt:SigningKey"] = "test-only-signing-key-at-least-32-characters-long-1234567890",
            ["Jwt:ExpirationMinutes"] = "60",

            // MockCrmGateway may only run in Development/Testing
            // (CrmGatewaySafety), so a Production host would refuse to start
            // with the repository's default Crm:Provider of "Mock". Naming a
            // different provider satisfies that startup guard; the gateway
            // itself is never resolved by these tests, which only read the
            // Swagger surface.
            ["Crm:Provider"] = environment == "Production" ? "NotMock" : "Mock"
        }));

        builder.ConfigureServices(services =>
        {
            // Same removal set as TigerCsApiFactory: EF Core refuses to build
            // its internal service provider when two database providers are
            // registered, so every descriptor the SqlServer registration
            // added has to go, not just DbContextOptions<T>.
            var efCoreDescriptors = services
                .Where(d => (d.ServiceType.FullName ?? string.Empty).Contains("EntityFrameworkCore", StringComparison.Ordinal)
                    || d.ServiceType == typeof(TigerCsDbContext)
                    || d.ServiceType == typeof(DbContextOptions<TigerCsDbContext>)
                    || d.ServiceType == typeof(DbContextOptions))
                .ToList();
            foreach (var descriptor in efCoreDescriptors)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<TigerCsDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        });
    }
}

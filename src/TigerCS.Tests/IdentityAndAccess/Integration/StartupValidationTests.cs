using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Tests.IdentityAndAccess.Integration;

/// <summary>
/// Review item 4/8: the app must fail fast at startup on insecure or missing
/// JWT/security configuration, rather than accepting a default or only
/// failing lazily on the first request.
/// </summary>
public class StartupValidationTests
{
    private sealed class ConfiguredFactory(
        string environment, Dictionary<string, string?> overrides, bool useInMemoryDb = true)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(overrides));

            if (useInMemoryDb)
            {
                builder.ConfigureServices(services =>
                {
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

                    services.AddDbContext<TigerCsDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
                });
            }
        }
    }

    private static Dictionary<string, string?> ValidConfig() => new()
    {
        ["ConnectionStrings:TigerCsDatabase"] = "Server=(unused-for-tests);Database=(unused-for-tests);",
        ["Jwt:Issuer"] = "TigerCS.Tests",
        ["Jwt:Audience"] = "TigerCS.Tests.Client",
        ["Jwt:SigningKey"] = "test-only-signing-key-at-least-32-characters-long-1234567890",
        ["Jwt:ExpirationMinutes"] = "60"
    };

    [Fact]
    public void MissingSigningKey_FailsAtStartup()
    {
        var config = ValidConfig();
        config.Remove("Jwt:SigningKey");
        using var factory = new ConfiguredFactory("Testing", config);

        var ex = Assert.ThrowsAny<Exception>(() => factory.Server);
        Assert.Contains("Jwt:SigningKey", ex.ToString());
    }

    [Fact]
    public void SigningKeyShorterThan32Bytes_FailsAtStartup()
    {
        var config = ValidConfig();
        config["Jwt:SigningKey"] = "too-short-key"; // 13 bytes, well under 32
        using var factory = new ConfiguredFactory("Testing", config);

        var ex = Assert.ThrowsAny<Exception>(() => factory.Server);
        Assert.Contains("32 bytes", ex.ToString());
    }

    [Fact]
    public void MissingIssuerOrAudience_FailsAtStartup()
    {
        var config = ValidConfig();
        config.Remove("Jwt:Issuer");
        using var factory = new ConfiguredFactory("Testing", config);

        var ex = Assert.ThrowsAny<Exception>(() => factory.Server);
        Assert.Contains("Jwt:Issuer", ex.ToString());
    }

    /// <summary>
    /// Superseded by the CRM Verification increment. PR #9's original
    /// version of this test proved Production wasn't unconditionally
    /// refused at the JWT/security-config level, by ASPNETCORE_ENVIRONMENT
    /// name alone — that decision (no blanket "if IsProduction() throw")
    /// still stands and is unchanged. What's new: MockCrmGateway must never
    /// run in Production (explicit review requirement) — Program.cs now
    /// fails fast if Crm:Provider resolves to "Mock" outside
    /// Development/Testing, which is <c>CrmGatewayOptions</c>' own default
    /// and every environment's config today (no real ICrmGateway
    /// implementation exists yet, per backlog S-06). Given valid JWT config
    /// (the same config that proves other environments start cleanly, see
    /// <see cref="ValidConfiguration_StartsSuccessfully"/>) and the default
    /// "Mock" provider, Production therefore genuinely cannot start today —
    /// a correct outcome, not a regression: it is consistent with "no
    /// production deployment is authorized at this pilot stage" (ADR-0022,
    /// docs/DEV-SETUP.md), now enforced by a real, narrow, risk-specific
    /// code gate rather than documentation alone. This guard is conditional
    /// on the selected gateway type, not the environment name itself — a
    /// non-Mock provider starts cleanly in Production, see
    /// <see cref="ProductionEnvironment_WithNonMockCrmProvider_StartsSuccessfully"/>.
    /// </summary>
    [Fact]
    public void ProductionEnvironment_WithMockCrmProvider_FailsAtStartup()
    {
        using var factory = new ConfiguredFactory("Production", ValidConfig());

        var ex = Assert.ThrowsAny<Exception>(() => factory.Server);
        Assert.Contains("Crm:Provider", ex.ToString());
        Assert.Contains("Mock", ex.ToString());
    }

    /// <summary>
    /// Final correction: proves the guard is conditional on the selected
    /// gateway type (Crm:Provider), not simply the environment name — a
    /// non-Mock provider must be able to start in Production, since a
    /// future real InternalCrmGateway configuration needs to run there.
    /// <see cref="TigerCS.Integrations.Modules.CrmIntegration.CrmGatewaySafety.IsUnsafe"/>
    /// only ever flags "Mock" outside Development/Testing (see its own unit
    /// tests, CrmGatewaySafetyTests) — a literal, not-yet-implemented
    /// provider value like this test's is registered lazily
    /// (IntegrationsServiceCollectionExtensions) and so does not prevent the
    /// host itself from starting; it would only fail a request that
    /// actually resolves <c>ICrmGateway</c>, which this test never makes.
    /// </summary>
    [Fact]
    public void ProductionEnvironment_WithNonMockCrmProvider_StartsSuccessfully()
    {
        var config = ValidConfig();
        config["Crm:Provider"] = "InternalCrmGateway";
        using var factory = new ConfiguredFactory("Production", config);

        var server = factory.Server;

        Assert.NotNull(server);
    }

    [Fact]
    public void PasswordPolicyBelowFloor_FailsAtStartup()
    {
        var config = ValidConfig();
        config["Identity:Password:RequiredLength"] = "4"; // below the 8-char floor
        using var factory = new ConfiguredFactory("Testing", config);

        var ex = Assert.ThrowsAny<Exception>(() => factory.Server);
        Assert.Contains("RequiredLength", ex.ToString());
    }

    [Fact]
    public void ValidConfiguration_StartsSuccessfully()
    {
        using var factory = new ConfiguredFactory("Testing", ValidConfig());

        var server = factory.Server;

        Assert.NotNull(server);
    }
}

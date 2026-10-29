using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tiger.TigerGroupWeb.Genesys;

namespace Tiger.TigerGroupWeb.Tests;

// TEMPORARY - remove together with TicketingConfigDiagnostics and the Log401 calls in TicketingGenesysService.
public class TicketingDiagnosticsTests
{
    private const string FilePassword = "FILE-PASSWORD-MUST-NEVER-BE-LOGGED";
    private const string OtherPassword = "OTHER-PASSWORD-MUST-NEVER-BE-LOGGED";

    private static (TicketingGenesysService Service, FakeTigerCs Upstream, CapturedLogs Logs) NewService()
    {
        var upstream = new FakeTigerCs();
        var logs = new CapturedLogs();
        var logger = new Logger<TicketingGenesysService>(LoggerFactory.Create(b => b.AddProvider(logs)));
        var service = new TicketingGenesysService(
            upstream, new MemoryCache(new MemoryCacheOptions()), Options.Create(new TicketingOptions()), logger);
        return (service, upstream, logs);
    }

    [Fact]
    public async Task ALoginRefusedWith401IsLoggedAsTheLoginOperationAndNothingElse()
    {
        var (service, upstream, logs) = NewService();
        upstream.LoginStatus = _ => HttpStatusCode.Unauthorized;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CustomerLookupAsync("0501234567"));

        Assert.Contains("operation=Login (POST /api/auth/login)", logs.All);
        Assert.DoesNotContain("0501234567", logs.All);
        Assert.DoesNotContain(new TicketingOptions().Password, logs.All);
        Assert.DoesNotContain(new TicketingOptions().Username, logs.All);
    }

    [Fact]
    public async Task ALookupRefusedWith401AfterALoginIsLoggedAsTheLookupWithoutTheQuery()
    {
        var (service, upstream, logs) = NewService();
        upstream.Script = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        using var response = await service.CustomerLookupAsync("0501234567");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("operation=Customer Lookup (GET /api/genesys/customers/lookup) after successful login", logs.All);
        Assert.DoesNotContain("0501234567", logs.All);
        Assert.DoesNotContain("phoneNumber", logs.All);
    }

    private static (IServiceProvider Services, CapturedLogs Logs, string Dir) Build(string? overridePassword)
    {
        var dir = Directory.CreateTempSubdirectory("ticketing-diag").FullName;
        File.WriteAllText(Path.Combine(dir, "appsettings.Production.json"),
            "{ \"Ticketing\": { \"BaseUrl\": \"https://prod.test\", \"Username\": \"svc\", \"Password\": \"" + FilePassword + "\" } }");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = dir, EnvironmentName = "Production" });
        if (overridePassword is not null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Ticketing:Password"] = overridePassword });
        }

        var logs = new CapturedLogs();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.Configure<TicketingOptions>(builder.Configuration.GetSection("Ticketing"));
        return (builder.Build().Services, logs, dir);
    }

    [Fact]
    public void ReportsTheFileAsTheSourceAndATrueMatchWithoutLoggingAnySecret()
    {
        var (services, logs, dir) = Build(overridePassword: null);
        try
        {
            TicketingConfigDiagnostics.Log(services);

            Assert.Contains("Environment=Production", logs.All);
            Assert.Contains("BaseUrl=https://prod.test", logs.All);
            Assert.Contains("Username=svc", logs.All);
            Assert.Contains("appsettings.Production.json", logs.All);
            Assert.Contains("PasswordMatchesAppsettingsProduction=true", logs.All);
            Assert.DoesNotContain(FilePassword, logs.All);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReportsAnOverridingProviderAndAFalseMatchWithoutLoggingAnySecret()
    {
        var (services, logs, dir) = Build(OtherPassword);
        try
        {
            TicketingConfigDiagnostics.Log(services);

            Assert.Contains("MemoryConfigurationProvider", logs.All);
            Assert.Contains("PasswordMatchesAppsettingsProduction=false", logs.All);
            Assert.DoesNotContain(FilePassword, logs.All);
            Assert.DoesNotContain(OtherPassword, logs.All);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NeverThrowsWhenTheServicesItNeedsAreMissing()
    {
        var empty = new ServiceCollection().BuildServiceProvider();

        TicketingConfigDiagnostics.Log(empty);
    }
}

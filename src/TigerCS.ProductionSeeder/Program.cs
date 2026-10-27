
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Seed;
using TigerCS.Infrastructure.Persistence;

const string expectedServer = "TGAESHJDBP02";
const string expectedDatabase = "TigerCS_Ticketing_SeedTest";

var validArgs =
    args.Length == 1 && args[0] == "validate" ||
    args.Length == 2 && args[0] == "seed" &&
    args[1] == "--confirm-seed-test";

if (!validArgs)
{
    Console.WriteLine(
        "Usage: validate | seed --confirm-seed-test");
    return 1;
}

var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();

var connectionString =
    configuration.GetConnectionString("TigerCsDatabase");

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Missing connection string.");
    return 1;
}

var cs = new SqlConnectionStringBuilder(connectionString);

if (!string.Equals(cs.InitialCatalog, expectedDatabase,
    StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("BLOCKED: Wrong database.");
    return 1;
}

var services = new ServiceCollection();

services.AddLogging(x => x.AddConsole());
services.AddSingleton<IConfiguration>(configuration);

services.AddDbContext<TigerCsDbContext>(options =>
    options.UseSqlServer(connectionString));

services.AddIdentityCore<ApplicationUser>()
    .AddRoles<ApplicationRole>()
    .AddEntityFrameworkStores<TigerCsDbContext>();

using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();

var db = scope.ServiceProvider
    .GetRequiredService<TigerCsDbContext>();

try
{
    await db.Database.OpenConnectionAsync();

    var connection = (SqlConnection)db.Database.GetDbConnection();

    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT @@SERVERNAME, DB_NAME()";

    string server;
    string database;

    await using (var reader = await command.ExecuteReaderAsync())
    {
        if (!await reader.ReadAsync())
            throw new Exception("Could not identify SQL Server.");

        server = reader.GetString(0);
        database = reader.GetString(1);
    }

    if (!string.Equals(server, expectedServer,
            StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(database, expectedDatabase,
            StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("BLOCKED: Unexpected SQL target.");
        return 1;
    }

    var applied = (await db.Database
        .GetAppliedMigrationsAsync()).OrderBy(x => x).ToArray();

    var expected = db.Database
        .GetMigrations()
        .OrderBy(x => x)
        .ToArray();
    if (expected.Length == 0 ||
        !applied.SequenceEqual(expected))
    {
        Console.Error.WriteLine(
            "BLOCKED: Migration history mismatch.");
        return 1;
    }

    Console.WriteLine($"Server: {server}");
    Console.WriteLine($"Database: {database}");
    Console.WriteLine($"Migrations: {applied.Length}");

    if (args[0] == "validate")
    {
        Console.WriteLine("Validation successful. No changes made.");
        return 0;
    }



    var roles = scope.ServiceProvider
        .GetRequiredService<RoleManager<ApplicationRole>>();

    if (await db.Departments.AnyAsync() ||
        await db.Priorities.AnyAsync() ||
        await db.RequestTypes.AnyAsync() ||
        await db.Workflows.AnyAsync() ||
        roles.Roles.Any())
    {
        Console.Error.WriteLine(
            "BLOCKED: SeedTest already contains reference data.");
        return 1;
    }

    Console.WriteLine("Starting SeedTest reference data...");

    await ProductionReferenceData.SeedAsync(
        scope.ServiceProvider);

    Console.WriteLine("SeedTest completed successfully.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.ToString());
    Console.Error.WriteLine(
        "Seed failed. Inspect SeedTest before retrying.");
    return 1;
}

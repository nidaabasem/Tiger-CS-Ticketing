using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TigerCS.Infrastructure.Persistence;

/// <summary>
/// Lets `dotnet ef migrations add/remove` and `dotnet ef database update`
/// run without spinning up the full Api host. Only used by EF Core's
/// design-time tooling — never by the running application (Program.cs wires
/// TigerCsDbContext from configuration instead). The connection string here
/// is read from the TIGERCS_DESIGN_TIME_CONNECTION environment variable,
/// falling back to a local SQL Server default that matches docs/DEV-SETUP.md
/// — never a real/shared database.
/// </summary>
public sealed class TigerCsDbContextFactory : IDesignTimeDbContextFactory<TigerCsDbContext>
{
    public TigerCsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("TIGERCS_DESIGN_TIME_CONNECTION")
            ?? "Server=localhost,1433;Database=TigerCsTicketing_Dev;User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=True;";

        var optionsBuilder = new DbContextOptionsBuilder<TigerCsDbContext>()
            .UseSqlServer(connectionString);

        return new TigerCsDbContext(optionsBuilder.Options);
    }
}

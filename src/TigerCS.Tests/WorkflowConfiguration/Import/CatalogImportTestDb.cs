using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Import;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Tests.WorkflowConfiguration.Import;

/// <summary>
/// A real relational database (SQLite in memory) carrying what a deployed
/// environment has before the catalog import: the fixed priorities, Customer
/// Service, and the exact workflow reference seed (Collections, Registration,
/// Handover, Call Center, provisional Accounting, and their request types).
/// Relational on purpose: the import's transaction, the filtered unique index
/// on RequestTypes.Code and the foreign keys are all real here.
/// </summary>
internal sealed class CatalogImportTestDb : IDisposable
{
    public static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

    public const string FacilitiesManagement = "Facilities Management";
    public const string LeasingCustomerServices = "Leasing Customer Services";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    private CatalogImportTestDb()
    {
        _connection.Open();
    }

    /// <param name="additionalDepartments">Departments beyond the reference seed — the workbook also names Facilities Management and Leasing Customer Services.</param>
    public static async Task<CatalogImportTestDb> CreateAsync(params string[] additionalDepartments)
    {
        var db = new CatalogImportTestDb();
        await using var context = db.CreateContext();
        await context.Database.EnsureCreatedAsync();

        foreach (var level in Enum.GetValues<PriorityLevel>())
        {
            context.Priorities.Add(new Priority((byte)level, level.ToString(), (byte)level));
        }

        context.Departments.Add(new Department("Customer Service", WorkflowReferenceData.CustomerServiceCode));
        foreach (var (name, index) in additionalDepartments.Select((n, i) => (n, i)))
        {
            context.Departments.Add(new Department(name, $"X{index}"));
        }

        await context.SaveChangesAsync();
        await WorkflowReferenceData.SeedAsync(context);
        return db;
    }

    public static Task<CatalogImportTestDb> CreateWithAllCatalogDepartmentsAsync() =>
        CreateAsync(FacilitiesManagement, LeasingCustomerServices);

    public TigerCsDbContext CreateContext() => new SqliteTigerCsDbContext(
        new DbContextOptionsBuilder<TigerCsDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning))
            .Options);

    public async Task<RequestTypeCatalogImportReport> ImportAsync(bool apply = true, bool activateResolved = true)
    {
        await using var context = CreateContext();
        return await RequestTypeCatalogImporter.ImportAsync(
            context, RequestTypeCatalog.Load(), new RequestTypeCatalogImportOptions(Now, apply, activateResolved));
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>SQL Server generates Tickets.RowVersion; SQLite cannot, so the SQLite schema gives it a default. Nothing under test changes.</summary>
    private sealed class SqliteTigerCsDbContext(DbContextOptions<TigerCsDbContext> options) : TigerCsDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Ticket>().Property(t => t.RowVersion).HasDefaultValueSql("X'0000000000000000'");
        }
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
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

    private readonly bool _legacySchema;

    private CatalogImportTestDb(bool legacySchema)
    {
        _legacySchema = legacySchema;
        _connection.Open();
    }

    /// <param name="additionalDepartments">Departments beyond the reference seed — the workbook also names Facilities Management and Leasing Customer Services.</param>
    public static Task<CatalogImportTestDb> CreateAsync(params string[] additionalDepartments) =>
        CreateAsync(legacySchema: false, additionalDepartments);

    /// <summary>
    /// A database the AddRequestTypeCatalogImport migration has NOT reached:
    /// its tables genuinely lack the new columns, so any query that touched
    /// one would fail with "no such column".
    /// </summary>
    public static Task<CatalogImportTestDb> CreateBeforeMigrationAsync() =>
        CreateAsync(legacySchema: true, FacilitiesManagement, LeasingCustomerServices);

    private static async Task<CatalogImportTestDb> CreateAsync(bool legacySchema, params string[] additionalDepartments)
    {
        var db = new CatalogImportTestDb(legacySchema);
        await using var context = db.CreateSchemaContext();
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

    /// <summary>The application's real model — what the importer and the Api use.</summary>
    public TigerCsDbContext CreateContext() => new SqliteTigerCsDbContext(Options(), legacySchema: false);

    private TigerCsDbContext CreateSchemaContext() => new SqliteTigerCsDbContext(Options(), _legacySchema);

    private DbContextOptions<TigerCsDbContext> Options() =>
        new DbContextOptionsBuilder<TigerCsDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning))
            // The legacy-schema context has a different model on purpose.
            .EnableServiceProviderCaching(false)
            .Options;

    /// <summary>The first-UAT-import settings: applied, everything created stays inactive, existing types untouched.</summary>
    public static RequestTypeCatalogImportOptions FirstImport => new(Now, Apply: true);

    /// <summary>A later import after the business answered the priority-change question and asked for activation.</summary>
    public static RequestTypeCatalogImportOptions ActivatingImport =>
        new(Now, Apply: true, ActivateResolved: true, AllowAgentPriorityChange: true);

    public async Task<RequestTypeCatalogImportReport> ImportAsync(RequestTypeCatalogImportOptions? options = null)
    {
        await using var context = CreateContext();
        return await RequestTypeCatalogImporter.ImportAsync(context, RequestTypeCatalog.Load(), options ?? FirstImport);
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>SQL Server generates Tickets.RowVersion; SQLite cannot, so the SQLite schema gives it a default. Nothing under test changes.</summary>
    private sealed class SqliteTigerCsDbContext(DbContextOptions<TigerCsDbContext> options, bool legacySchema) : TigerCsDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Ticket>().Property(t => t.RowVersion).HasDefaultValueSql("X'0000000000000000'");

            if (legacySchema)
            {
                // Exactly the columns the migration adds.
                builder.Entity<RequestType>().Ignore(r => r.Code).Ignore(r => r.RequestGroup)
                    .Ignore(r => r.Description).Ignore(r => r.RequiredDocumentsJson);
                builder.Entity<RequestTypeSlaPolicy>().Ignore(p => p.FirstResponseUnit);
                builder.Entity<WorkflowTemplateStep>().Ignore(s => s.DepartmentId);

                // ...and those AddConfiguredRuntimeEnforcement adds.
                builder.Entity<RequestType>().Ignore(r => r.ConfigurationEnforced);
                builder.Entity<Ticket>().Ignore(t => t.CurrentWorkflowStepId);
                builder.Ignore<RequestTypeCatalogDecision>();
            }
        }
    }
}

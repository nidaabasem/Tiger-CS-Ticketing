using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Import;

/// <summary>
/// The one way the catalog import reaches a real database — run by hand,
/// never at startup:
/// <code>
/// dotnet TigerCS.Api.dll --import-request-types                 # dry run: prints the report, writes nothing
/// dotnet TigerCS.Api.dll --import-request-types --report out.md # dry run, report also saved to a file
/// dotnet TigerCS.Api.dll --import-request-types --apply         # writes, in one transaction
/// dotnet TigerCS.Api.dll --import-request-types --apply --keep-all-inactive
/// </code>
/// It uses the host's normal configuration (ConnectionStrings:TigerCsDatabase),
/// so the target database is whatever that environment's settings name —
/// check it before adding <c>--apply</c>.
/// </summary>
public static class RequestTypeCatalogCommand
{
    public const string Switch = "--import-request-types";
    public const string ApplySwitch = "--apply";
    public const string KeepAllInactiveSwitch = "--keep-all-inactive";
    public const string ReportSwitch = "--report";

    /// <summary>The migration that adds the columns the import writes; the import refuses to run before it.</summary>
    public const string RequiredMigration = "20260928085727_AddRequestTypeCatalogImport";

    public static bool IsRequested(IReadOnlyList<string> args) => args.Contains(Switch, StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(IServiceProvider services, IReadOnlyList<string> args, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        var apply = args.Contains(ApplySwitch, StringComparer.OrdinalIgnoreCase);
        var keepAllInactive = args.Contains(KeepAllInactiveSwitch, StringComparer.OrdinalIgnoreCase);
        var reportIndex = args.ToList().FindIndex(a => string.Equals(a, ReportSwitch, StringComparison.OrdinalIgnoreCase));
        var reportPath = reportIndex >= 0 && reportIndex + 1 < args.Count ? args[reportIndex + 1] : null;

        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();

        if (dbContext.Database.IsRelational())
        {
            var pending = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken);
            if (pending.Contains(RequiredMigration))
            {
                await output.WriteLineAsync(
                    $"Migration {RequiredMigration} has not been applied to this database; apply it before importing.");
                return 2;
            }
        }

        var report = await RequestTypeCatalogImporter.ImportAsync(
            dbContext,
            RequestTypeCatalog.Load(),
            new RequestTypeCatalogImportOptions(DateTime.UtcNow, Apply: apply, ActivateResolved: !keepAllInactive),
            cancellationToken);

        var markdown = report.ToMarkdown();
        await output.WriteLineAsync(markdown);
        if (reportPath is not null)
        {
            await File.WriteAllTextAsync(reportPath, markdown, cancellationToken);
        }

        return 0;
    }
}

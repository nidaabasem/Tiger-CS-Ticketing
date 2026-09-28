using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Import;

/// <summary>
/// The one way the catalog import reaches a real database — run by hand,
/// never at startup:
/// <code>
/// dotnet TigerCS.Api.dll --import-request-types --report out.md               # dry run; works before the migration too
/// dotnet TigerCS.Api.dll --import-request-types --apply --keep-all-inactive   # first import: everything created stays inactive
/// dotnet TigerCS.Api.dll --import-request-types --apply --activate-resolved --agent-priority-change allow|deny
/// dotnet TigerCS.Api.dll --import-request-types ... --link-existing           # also attach codes to same-named existing types
/// </code>
/// <c>--apply</c> must name its activation choice explicitly
/// (<c>--keep-all-inactive</c> or <c>--activate-resolved</c>) so nothing
/// goes live by omission. It uses the host's normal configuration
/// (ConnectionStrings:TigerCsDatabase), so the target database is whatever
/// that environment's settings name — check it before adding <c>--apply</c>.
/// </summary>
public static class RequestTypeCatalogCommand
{
    public const string Switch = "--import-request-types";
    public const string ApplySwitch = "--apply";
    public const string KeepAllInactiveSwitch = "--keep-all-inactive";
    public const string ActivateResolvedSwitch = "--activate-resolved";
    public const string LinkExistingSwitch = "--link-existing";
    public const string AgentPriorityChangeSwitch = "--agent-priority-change";
    public const string ReportSwitch = "--report";

    /// <summary>
    /// The latest migration the import writes to (the catalog columns come
    /// with AddRequestTypeCatalogImport, the decisions table with this one);
    /// applying refuses to run before it.
    /// </summary>
    public const string RequiredMigration = "20260928102230_AddConfiguredRuntimeEnforcement";

    public static bool IsRequested(IReadOnlyList<string> args) => args.Contains(Switch, StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(IServiceProvider services, IReadOnlyList<string> args, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        bool Has(string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);
        string? ValueOf(string flag)
        {
            var index = args.ToList().FindIndex(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
        }

        var apply = Has(ApplySwitch);
        var keepAllInactive = Has(KeepAllInactiveSwitch);
        var activateResolved = Has(ActivateResolvedSwitch);
        if (keepAllInactive && activateResolved)
        {
            await output.WriteLineAsync($"{KeepAllInactiveSwitch} and {ActivateResolvedSwitch} contradict each other; give one.");
            return 3;
        }

        if (apply && !keepAllInactive && !activateResolved)
        {
            await output.WriteLineAsync($"{ApplySwitch} needs an explicit activation choice: add {KeepAllInactiveSwitch} (recommended for a first import) or {ActivateResolvedSwitch}.");
            return 3;
        }

        bool? allowAgentPriorityChange = null;
        if (ValueOf(AgentPriorityChangeSwitch) is { } answer)
        {
            allowAgentPriorityChange = answer.ToLowerInvariant() switch
            {
                "allow" => true,
                "deny" => false,
                _ => null
            };
            if (allowAgentPriorityChange is null)
            {
                await output.WriteLineAsync($"{AgentPriorityChangeSwitch} takes 'allow' or 'deny'.");
                return 3;
            }
        }

        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();

        var schemaApplied = true;
        if (dbContext.Database.IsRelational())
        {
            var pending = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken);
            schemaApplied = !pending.Contains(RequiredMigration);
            if (!schemaApplied && apply)
            {
                await output.WriteLineAsync(
                    $"Migration {RequiredMigration} has not been applied to this database; apply it before {ApplySwitch}. A dry run works without it.");
                return 2;
            }
        }

        var report = await RequestTypeCatalogImporter.ImportAsync(
            dbContext,
            RequestTypeCatalog.Load(),
            new RequestTypeCatalogImportOptions(
                DateTime.UtcNow,
                Apply: apply,
                ActivateResolved: activateResolved,
                LinkExisting: Has(LinkExistingSwitch),
                AllowAgentPriorityChange: allowAgentPriorityChange,
                SchemaApplied: schemaApplied),
            cancellationToken);

        var markdown = report.ToMarkdown();
        await output.WriteLineAsync(markdown);
        if (ValueOf(ReportSwitch) is { } reportPath)
        {
            await File.WriteAllTextAsync(reportPath, markdown, cancellationToken);
        }

        return 0;
    }
}

using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// Registers the SLA background-job mechanism of ADR-0015 — Hangfire, backed
/// by the same SQL Server database (ADR-0003).
///
/// <para>
/// <b>Breach detection never depends on a connected client.</b> Both paths
/// are server-side background work against stored due timestamps: neither an
/// open browser nor a SignalR connection participates, and ADR-0016 confines
/// SignalR to state-change notification specifically so that it is not
/// load-bearing here.
/// </para>
/// </summary>
public static class BackgroundJobServiceCollectionExtensions
{
    public static IServiceCollection AddTigerCsBackgroundJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BackgroundJobOptions>(configuration.GetSection(BackgroundJobOptions.SectionName));

        var options = configuration.GetSection(BackgroundJobOptions.SectionName).Get<BackgroundJobOptions>()
            ?? new BackgroundJobOptions();

        // Registered whether or not Hangfire runs, so a fired job resolves
        // the same way in every environment.
        services.AddScoped<SlaDeadlineCheckJob>();
        services.AddScoped<SlaSweepJob>();

        if (!options.Enabled)
        {
            // See NoOpSlaDeadlineScheduler's remarks: detection logic is
            // unaffected, only its timing.
            services.AddSingleton<ISlaDeadlineScheduler, NoOpSlaDeadlineScheduler>();
            return services;
        }

        var connectionString = configuration.GetConnectionString("TigerCsDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'TigerCsDatabase' is required when BackgroundJobs:Enabled is true — Hangfire shares the "
                + "application database (ADR-0003/ADR-0015). See docs/DEV-SETUP.md.");

        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
            {
                // Hangfire's own tables live in their own schema so they are
                // never confused with, or migrated alongside, the
                // application schema EF Core owns.
                SchemaName = "HangfireSla",
                PrepareSchemaIfNecessary = true,
                QueuePollInterval = TimeSpan.FromSeconds(15),
                DisableGlobalLocks = true
            }));

        services.AddHangfireServer();
        services.AddSingleton<ISlaDeadlineScheduler, HangfireSlaDeadlineScheduler>();

        return services;
    }

    /// <summary>
    /// Registers SLA-Architecture.md §14's recurring safety sweep. Called
    /// after the host is built, because <see cref="IRecurringJobManager"/>
    /// needs a live storage connection.
    /// </summary>
    public static void UseTigerCsRecurringSlaSweep(this IServiceProvider services, BackgroundJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return;
        }

        // ADR-0015 / §14: "every 1–5 minutes". Clamped rather than trusted,
        // since a misconfigured interval would silently weaken the only
        // backstop behind the scheduled jobs.
        var minutes = Math.Clamp(options.SweepIntervalMinutes, 1, 5);

        services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<SlaSweepJob>(
            SlaSweepJob.RecurringJobId,
            job => job.RunAsync(CancellationToken.None),
            $"*/{minutes} * * * *");
    }
}

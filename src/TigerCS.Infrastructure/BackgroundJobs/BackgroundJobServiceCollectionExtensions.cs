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

        services.Configure<OutboxDispatchOptions>(configuration.GetSection(OutboxDispatchOptions.SectionName));

        // Registered whether or not Hangfire runs, so a fired job resolves
        // the same way in every environment.
        services.AddScoped<SlaDeadlineCheckJob>();
        services.AddScoped<SlaSweepJob>();
        services.AddScoped<OutboxDispatchJob>();

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
    /// Registers ADR-0013/ADR-0015's recurring Outbox dispatcher — the job
    /// that turns committed Outbox rows into outbound effects. Called after
    /// the host is built, for the same reason as the sweep below.
    ///
    /// <para>
    /// <b>Not registering it does not lose messages.</b> With
    /// <c>BackgroundJobs:Enabled</c> false (the test host, which has no SQL
    /// Server for Hangfire's own schema) Outbox rows still commit with their
    /// business transactions and stay <c>Pending</c> until something
    /// dispatches them — the switch governs when delivery happens, never
    /// whether the intent to deliver was recorded.
    /// </para>
    /// </summary>
    public static void UseTigerCsRecurringOutboxDispatch(
        this IServiceProvider services, BackgroundJobOptions backgroundJobOptions, OutboxDispatchOptions outboxOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(backgroundJobOptions);
        ArgumentNullException.ThrowIfNull(outboxOptions);

        if (!backgroundJobOptions.Enabled)
        {
            return;
        }

        // Clamped rather than trusted: a mistyped interval would silently
        // delay every customer-facing acknowledgement in the system, and an
        // interval of zero or negative would be rejected by Hangfire's cron
        // parser at startup rather than at a useful moment.
        var minutes = Math.Clamp(outboxOptions.PollIntervalMinutes, 1, 15);

        services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<OutboxDispatchJob>(
            OutboxDispatchJob.RecurringJobId,
            job => job.RunAsync(CancellationToken.None),
            $"*/{minutes} * * * *");
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

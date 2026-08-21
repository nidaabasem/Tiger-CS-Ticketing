namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// Configuration for the Hangfire-backed SLA jobs (ADR-0015). Bound from the
/// <c>BackgroundJobs</c> configuration section.
/// </summary>
public sealed class BackgroundJobOptions
{
    public const string SectionName = "BackgroundJobs";

    /// <summary>
    /// Whether Hangfire is started at all.
    ///
    /// <para>
    /// Hangfire's SQL Server storage connects and provisions its own schema
    /// on startup, which a test host running EF Core InMemory has no database
    /// for. Turning it off leaves breach detection itself fully wired and
    /// exercisable — the scheduler becomes
    /// <see cref="NoOpSlaDeadlineScheduler"/>, and
    /// <c>SlaBreachDetectionAppService</c> is unchanged — so the switch
    /// governs job <i>execution</i>, never job <i>logic</i>.
    /// </para>
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often SLA-Architecture.md §14's safety sweep runs. The approved
    /// range is every 1–5 minutes; the default sits at the slow end of it,
    /// because the sweep is a backstop and §13's per-deadline scheduled jobs
    /// are the primary path.
    /// </summary>
    public int SweepIntervalMinutes { get; set; } = 5;
}

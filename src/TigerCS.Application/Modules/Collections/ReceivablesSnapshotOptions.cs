namespace TigerCS.Application.Modules.Collections;

/// <summary>
/// Local receivables snapshot (section <c>Collections:ReceivablesSnapshot</c>). No credentials belong here: the Ticketing
/// connection string is <c>ConnectionStrings:TigerCsDatabase</c>, supplied through the deployment's secret store.
/// </summary>
public sealed class ReceivablesSnapshotOptions
{
    public const string SectionName = "Collections:ReceivablesSnapshot";

    /// <summary>True: pages read the local snapshot. False: the legacy direct PACT read is used (kept as an escape hatch).</summary>
    public bool UseLocalSnapshot { get; set; } = true;

    /// <summary>Registers the recurring Hangfire refresh. Requires <c>BackgroundJobs:Enabled</c> and a running Hangfire server (SQL Agent is NOT used).</summary>
    public bool RefreshEnabled { get; set; } = true;

    /// <summary>Hangfire cron for the refresh (server local time). Default: every 30 minutes.</summary>
    public string RefreshCron { get; set; } = "*/30 * * * *";

    /// <summary>Also trigger one refresh when the application starts so a new deployment is not empty until the first cron tick.</summary>
    public bool RefreshOnStartup { get; set; } = true;

    /// <summary>
    /// Freshness requirement. A company snapshot older than this is "Stale": previews show a warning and mark rows for review;
    /// campaign exports (review and Genesys) are REFUSED. Keep it above <see cref="RefreshCron"/> plus the longest expected refresh
    /// duration so one slow or failed run does not block exports. Default 90 minutes for a 30 minute cadence.
    /// </summary>
    public int MaxAgeMinutes { get; set; } = 90;

    /// <summary>Instalment due-date coverage requested from PACT. Wide on purpose: any supported From/To must be answerable.</summary>
    public DateTime SourceFromDate { get; set; } = new(2000, 1, 1);
    public DateTime SourceThroughDate { get; set; } = new(2099, 12, 31);

    /// <summary>
    /// Passed as @MinAmount. Keep 0: it is an int filter inside PACT, so any higher value permanently removes small positive balances.
    /// Zero-balance rows are dropped during staging validation instead.
    /// </summary>
    public int SourceMinAmount { get; set; }

    /// <summary>
    /// Keep fully paid instalments in the snapshot so the "Fully paid" and "All" views can be offered. Costs rows (the deployed procedures return every
    /// paid instalment since 2000 when called with MinAmount 0); set false to keep an outstanding-only snapshot - the two views are then disabled, never faked.
    /// </summary>
    public bool RetainPaidInstalments { get; set; } = true;

    /// <summary>
    /// Empty = the deployed dbo.p4AccountReceivables / dbo.p32AccountReceivables (output: remaining Amount + Status only, so Unpaid and Partially paid cannot be told apart).
    /// A companion suffix such as "V2" selects a separately deployed procedure that also returns the original and allocated amounts (see docs/Collections/pact-sql).
    /// Letters and digits only.
    /// </summary>
    public string SourceProcedureSuffix { get; set; } = "";

    /// <summary>Companion procedures only: true fails the refresh instead of guessing when a voucher maps to several tags/units.</summary>
    public bool StrictIdentity { get; set; }

    public int MaxRawRows { get; set; } = 1_000_000;

    /// <summary>A refresh that would shrink a company's snapshot by more than this percentage is rejected (previous snapshot kept).</summary>
    public int MaxShrinkPercent { get; set; } = 60;

    /// <summary>SQL command timeout of the refresh procedure (both PACT procedures run inside it, sequentially).</summary>
    public int RefreshCommandTimeoutSeconds { get; set; } = 1800;

    public int ReadCommandTimeoutSeconds { get; set; } = 60;

    public string ConnectionStringName { get; set; } = "TigerCsDatabase";
}

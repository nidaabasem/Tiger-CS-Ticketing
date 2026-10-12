namespace TigerCS.Application.Modules.Collections;

/// <summary>
/// Local receivables snapshot (section <c>Collections:ReceivablesSnapshot</c>): PACT -> <c>dbo.CollectionsRcvLine</c> every two hours, pages read the last published copy.
/// No credentials belong here: the Ticketing connection string is <c>ConnectionStrings:TigerCsDatabase</c>, supplied through the deployment's secret store.
/// </summary>
public sealed class ReceivablesSnapshotOptions
{
    public const string SectionName = "Collections:ReceivablesSnapshot";

    /// <summary>Registers the recurring Hangfire refreshes (one job per company). Needs <c>BackgroundJobs:Enabled</c> and a running Hangfire server (SQL Agent is NOT used).</summary>
    public bool RefreshEnabled { get; set; } = true;

    /// <summary>Hangfire cron of the refresh: every two hours, on the hour, in <see cref="RefreshTimeZoneId"/>.</summary>
    public string RefreshCron { get; set; } = "0 */2 * * *";

    /// <summary>The time zone the cron is evaluated in (explicit, never the server's local zone).</summary>
    public string RefreshTimeZoneId { get; set; } = "Asia/Dubai";

    /// <summary>Also trigger one refresh per company when the application starts so a new deployment is not empty until the first cron tick.</summary>
    public bool RefreshOnStartup { get; set; } = true;

    /// <summary>
    /// Freshness requirement. A company snapshot older than this is "Stale": previews show a warning and mark rows for review; exports (review and Genesys) are REFUSED.
    /// Must exceed the refresh interval (120 min) plus the longest expected refresh (<see cref="RefreshCommandTimeoutSeconds"/>, 30 min) plus slack, so a snapshot is never
    /// stale before its next normal refresh and one slow run does not block exports: 180 minutes. If the setting is lower the snapshot reads "Stale" in the hour before every refresh.
    /// </summary>
    public int MaxAgeMinutes { get; set; } = 180;

    /// <summary>Instalment due-date window requested from PACT. Wide on purpose: the history 2000-2099 is always stored; the pages decide what to show.</summary>
    public DateTime SourceFromDate { get; set; } = new(2000, 1, 1);
    public DateTime SourceThroughDate { get; set; } = new(2099, 12, 31);

    /// <summary>
    /// Passed as @MinAmount. Keep 0: it is an int filter inside PACT (Amount >= @MinAmount), so any higher value permanently removes small balances.
    /// The Minimum Total of the pages is NOT sent to PACT; it is applied to the summed payments of an apartment when a page is read.
    /// </summary>
    public int SourceMinAmount { get; set; }

    /// <summary>
    /// Accept the unit/account directory (every unit PACT's receivables procedure returned per tenant, fully paid ones included) as COMPLETE scope evidence for the payment statement. Default false: the procedure
    /// omits units without a payment plan / ledger row, and EDSM's tenant scope has not been compared with it. Switch on only once it is CONFIRMED (by PACT or a full reconciliation, not a few samples) that the source lists every unit and contract of each account. While false, a statement is shown
    /// only when PACT's contract list for the customer's phone proves the account.
    /// </summary>
    public bool AccountDirectoryComplete { get; set; }

    public int MaxRawRows { get; set; } = 2_000_000;

    /// <summary>A refresh that would shrink a company's snapshot by more than this percentage is rejected (previous snapshot kept).</summary>
    public int MaxShrinkPercent { get; set; } = 60;

    /// <summary>SQL command timeout of ONE company's refresh (the PACT call runs inside it). A hung PACT call ends the attempt as Failed at this limit.</summary>
    public int RefreshCommandTimeoutSeconds { get; set; } = 1800;

    public int ReadCommandTimeoutSeconds { get; set; } = 60;

    public string ConnectionStringName { get; set; } = "TigerCsDatabase";
}

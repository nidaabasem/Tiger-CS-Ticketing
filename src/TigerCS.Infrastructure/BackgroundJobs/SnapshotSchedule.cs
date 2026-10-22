using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// Reports whether the 2-hourly snapshot refresh is actually scheduled in this process, from the SAME two settings the registration code reads
/// (<c>BackgroundJobs:Enabled</c> and <c>Collections:ReceivablesSnapshot:RefreshEnabled</c>), so the pages never imply a refresh that nothing will run.
/// </summary>
public sealed class SnapshotSchedule(BackgroundJobOptions jobs, ReceivablesSnapshotOptions snapshot) : ISnapshotSchedule
{
    public bool ScheduledRefreshActive => jobs.Enabled && snapshot.RefreshEnabled;

    public string Describe() =>
        !jobs.Enabled ? "Scheduled refresh is OFF on this server (BackgroundJobs:Enabled is false): the data only changes when someone presses Retry."
        : !snapshot.RefreshEnabled ? "Scheduled refresh is switched off (Collections:ReceivablesSnapshot:RefreshEnabled is false): the data only changes when someone presses Retry."
        : $"Scheduled refresh is ON: cron \"{snapshot.RefreshCron}\" ({snapshot.RefreshTimeZoneId}), one job per company{(snapshot.RefreshOnStartup ? ", and one run per company at every application start" : "")}.";
}

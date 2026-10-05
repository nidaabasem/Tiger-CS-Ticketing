using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Services;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// The one designated scheduler for Collections reminders: a Hangfire
/// recurring job on the application's existing Hangfire server (ADR-0015).
/// No other timer, hosted service or Genesys-side schedule decides when
/// TigerCS sends a reminder. It is registered only when automatic scheduling
/// is active (<c>CollectionsOptions.IsAutomaticSchedulingActive</c>) and
/// removed otherwise; <see cref="CollectionsReminderAppService.RunScheduledAsync"/>
/// re-checks the same switch, so a stale registration still sends nothing.
/// </summary>
public sealed class CollectionsReminderScheduleJob(
    CollectionsReminderAppService reminders, ILogger<CollectionsReminderScheduleJob> logger)
{
    public const string RecurringJobId = "collections-payment-reminders";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var result = await reminders.RunScheduledAsync(cancellationToken);
        if (!result.Ran)
        {
            logger.LogInformation("Collections reminder schedule skipped: {Detail}", result.Detail);
            return;
        }

        logger.LogInformation(
            "Collections reminder schedule: {Created} queued, {AlreadyExisted} already sent this cycle, {Refused} refused{Truncated}. {Detail}",
            result.Created, result.AlreadyExisted, result.Refused, result.Truncated ? " (scan truncated)" : "", result.Detail ?? "");
    }
}

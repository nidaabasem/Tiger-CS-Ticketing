using System.Globalization;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>One-off background load of a due-date range the snapshot does not cover (dates travel as ISO strings: Hangfire-safe).</summary>
public sealed class CollectionsReceivablesRangeLoadJob(IReceivablesRefresher refresher, ILogger<CollectionsReceivablesRangeLoadJob> logger)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(string from, string through, CancellationToken cancellationToken)
    {
        var result = await refresher.RefreshAsync("RangeLoad", null, cancellationToken,
            DateOnly.ParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateOnly.ParseExact(through, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        logger.LogInformation("Receivables range load {From}..{Through}: {Status}.", from, through, result.Status);
        if (result.Status is "PartialFailure" or "Failed")
            throw new InvalidOperationException($"Receivables range load {from}..{through} {result.Status}; the previous snapshot is unchanged for failed companies.");
    }
}

/// <summary>Runs the load as a Hangfire background job (BackgroundJobs:Enabled).</summary>
public sealed class HangfireReceivablesRangeLoader(IBackgroundJobClient client) : IReceivablesRangeLoader
{
    public Task<bool> EnqueueAsync(DateOnly from, DateOnly through, CancellationToken cancellationToken)
    {
        client.Enqueue<CollectionsReceivablesRangeLoadJob>(job => job.RunAsync(
            from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), through.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), CancellationToken.None));
        return Task.FromResult(true);
    }
}

/// <summary>
/// Fallback when Hangfire is disabled: runs the same load on a background task of this process (not tied to the request). The
/// refresh procedure's application lock still prevents overlap; a restart abandons a running load (the next one supersedes it).
/// </summary>
public sealed class InProcessReceivablesRangeLoader(IServiceScopeFactory scopes, ILogger<InProcessReceivablesRangeLoader> logger) : IReceivablesRangeLoader
{
    public Task<bool> EnqueueAsync(DateOnly from, DateOnly through, CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<IReceivablesRefresher>().RefreshAsync("RangeLoad", null, CancellationToken.None, from, through);
                logger.LogInformation("Receivables range load {From:yyyy-MM-dd}..{Through:yyyy-MM-dd}: {Status}.", from, through, result.Status);
            }
            catch (Exception ex) { logger.LogError(ex, "Receivables range load failed."); }
        }, CancellationToken.None);
        return Task.FromResult(true);
    }
}

using System.Globalization;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;

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

/// <summary>Retry of ONE company (4 or 32) over the standard window, e.g. after its scheduled refresh failed.</summary>
public sealed class CollectionsReceivablesCompanyRetryJob(IReceivablesRefresher refresher, ILogger<CollectionsReceivablesCompanyRetryJob> logger)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(int companyId, CancellationToken cancellationToken)
    {
        var result = await refresher.RefreshAsync("Retry", companyId, cancellationToken);
        logger.LogInformation("Receivables retry for company {CompanyId}: {Status}.", companyId, result.Status);
        foreach (var company in result.Companies.Where(c => c.Status != "Succeeded"))
            logger.LogWarning("Receivables retry company {CompanyId} FAILED (error {ErrorNumber}): {ErrorMessage}", company.CompanyId, company.ErrorNumber, company.ErrorMessage);
        if (result.Status is "PartialFailure" or "Failed")
            throw new InvalidOperationException($"Receivables retry for company {companyId} {result.Status}; the previous snapshot is unchanged.");
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

    public Task<bool> EnqueueCompanyRefreshAsync(int companyId, CancellationToken cancellationToken)
    {
        client.Enqueue<CollectionsReceivablesCompanyRetryJob>(job => job.RunAsync(companyId, CancellationToken.None));
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

    public Task<bool> EnqueueCompanyRefreshAsync(int companyId, CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<IReceivablesRefresher>().RefreshAsync("Retry", companyId, CancellationToken.None);
                logger.LogInformation("Receivables retry for company {CompanyId}: {Status}.", companyId, result.Status);
                foreach (var company in result.Companies.Where(c => c.Status != "Succeeded"))
                    logger.LogWarning("Receivables retry company {CompanyId} FAILED (error {ErrorNumber}): {ErrorMessage}", company.CompanyId, company.ErrorNumber, company.ErrorMessage);
            }
            catch (Exception ex) { logger.LogError(ex, "Receivables retry for company {CompanyId} failed.", companyId); }
        }, CancellationToken.None);
        return Task.FromResult(true);
    }
}

/// <summary>Recurring reload of the bulk CRM owner feed (who holds an eligible sale of each unit). A failure keeps the previous data; the next tick retries.</summary>
public sealed class CollectionsCrmOwnersRefreshJob(CollectionsCrmOwnersRefreshService service, ILogger<CollectionsCrmOwnersRefreshJob> logger)
{
    public const string RecurringJobId = "collections-crm-owners-refresh";

    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var result = await service.RefreshAsync(cancellationToken);
        if (result.Succeeded) logger.LogInformation("CRM owners refreshed: {Stored} stored of {Fetched} fetched.", result.Stored, result.Fetched);
        else throw new InvalidOperationException($"CRM owners refresh failed: {result.Message}");
    }
}

using Hangfire;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;

namespace TigerCS.Infrastructure.BackgroundJobs;

/// <summary>
/// Recurring refresh of the local PACT receivables snapshot (SQL Server Agent is not used). The job is a thin caller: overlap
/// protection, staging, validation, per-company publication and "keep the previous snapshot on failure" live in
/// dbo.usp_Collections_RefreshReceivables. Hangfire retries are disabled because the next cron tick is the retry.
/// </summary>
public sealed class CollectionsReceivablesRefreshJob(IReceivablesRefresher refresher, ILogger<CollectionsReceivablesRefreshJob> logger)
{
    public const string RecurringJobId = "collections-receivables-refresh";

    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var result = await refresher.RefreshAsync("Hangfire", null, cancellationToken);
        foreach (var company in result.Companies)
        {
            if (company.Status == "Succeeded")
                logger.LogInformation("Receivables refresh company {CompanyId}: {Published} rows published ({Raw} read, {Zero} settled, {BadUnit} invalid unit, {BadIdentity} invalid identity); timings: fetch from PACT {FetchMs} ms, validate {ValidateMs} ms, publish {PublishMs} ms.",
                    company.CompanyId, company.PublishedRows, company.RawRows, company.ExcludedZeroRows, company.ExcludedInvalidUnitRows, company.ExcludedInvalidIdentityRows,
                    company.FetchMs, company.ValidateMs, company.PublishMs);
            else
                logger.LogWarning("Receivables refresh company {CompanyId} FAILED (error {ErrorNumber}); the previous snapshot is still served.", company.CompanyId, company.ErrorNumber);
        }
        switch (result.Status)
        {
            case "AlreadyRunning":
                logger.LogInformation("Receivables refresh skipped: {Message}", result.Message);
                break;
            case "Succeeded":
                logger.LogInformation("Receivables refresh {RunId} succeeded.", result.RunId);
                break;
            case "PartialFailure":
                // Surfaced as a failed Hangfire execution so the dashboard shows it; the snapshot of the healthy company WAS published.
                throw new InvalidOperationException($"Receivables refresh {result.RunId} partially failed; see the per-company log lines.");
            default:
                throw new InvalidOperationException($"Receivables refresh failed: {result.Message}");
        }
    }
}

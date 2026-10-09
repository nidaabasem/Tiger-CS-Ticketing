using System.Data.Common;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

/// <summary>
/// The explicit "refresh review data" job: one read of the financial source, validation of every record, and storage.
/// Starting one is a normal request (it only queues a run); the work happens in the background job with visible progress.
/// The review screen itself never reads PACT.
/// </summary>
public sealed class ReviewRefreshService(
    CollectionsOptions options, CollectionsCampaignOptions campaignOptions, PactReceivablesOptions sourceOptions,
    CollectionsReviewOptions reviewOptions, CollectionsAuthorizationService authorization, CollectionsClock clock,
    IPactReceivablesSource source, IReviewStore store, IReviewJobScheduler scheduler, ReviewQueryService query,
    ILogger<ReviewRefreshService> logger)
{
    public const string SourceLabel = "PACT receivables (companies 4 and 32)";

    /// <summary>Whether the reconciliation sign-off applies to the procedure that is configured now.</summary>
    public bool SourceReconciled =>
        campaignOptions.FinancialSourceValidated
        && string.Equals(campaignOptions.ValidatedProcedureSuffix ?? "", sourceOptions.ProcedureSuffix ?? "", StringComparison.Ordinal);

    public ReviewBuildContext Context(DateOnly asOf, DateTime readAtUtc) => new(asOf, readAtUtc, SourceLabel, sourceOptions.Currency,
        SourceReconciled, campaignOptions.LegalNoticeExportEnabled, campaignOptions.LegalCaseDispatchApproved,
        reviewOptions.FloatNoiseTolerance, sourceOptions.SourceStatusMap);

    public async Task<CollectionsResult<ReviewRunDto>> StartAsync(CollectionsCaller caller, int? companyId, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanReadFinancials)
            return CollectionsResult<ReviewRunDto>.Fail(CollectionsOutcome.Forbidden);
        if (!options.Enabled || !sourceOptions.Enabled) return CollectionsResult<ReviewRunDto>.Fail(CollectionsOutcome.Disabled);
        if (companyId is not (null or 4 or 32))
            return CollectionsResult<ReviewRunDto>.Fail(CollectionsOutcome.InvalidRequest, "Company must be 4 or 32.");

        var now = clock.UtcNow;
        var active = await store.GetActiveRunAsync(ct);
        if (active is not null && !IsDead(active, now)) return CollectionsResult<ReviewRunDto>.Ok(query.ToDto(active), CollectionsOutcome.Replayed);
        if (active is not null) await store.FailRunAsync(active.CollectionsReviewRunId, "The previous refresh did not finish and was replaced.", now, ct);

        var asOf = clock.BusinessDate;
        var run = new CollectionsReviewRun
        {
            Status = ReviewRunStatus.Queued, AsOfDate = asOf, CompanyId = companyId,
            DueFrom = DateOnly.FromDateTime(sourceOptions.StartDate),
            DueTo = new DateOnly(asOf.Year, asOf.Month, DateTime.DaysInMonth(asOf.Year, asOf.Month)),
            Source = SourceLabel, SourceProcedureSuffix = sourceOptions.ProcedureSuffix ?? "", SourceReconciled = SourceReconciled,
            RequestedByEmployeeId = caller.EmployeeId, RequestedAtUtc = now, Phase = "Queued"
        };
        if (!await store.TryAddRunAsync(run, ct))
        {
            // Lost the race with a concurrent click: report the run that won.
            var winner = await store.GetActiveRunAsync(ct);
            return winner is null
                ? CollectionsResult<ReviewRunDto>.Fail(CollectionsOutcome.FinanceUnavailable, "Could not queue the refresh. Retry.")
                : CollectionsResult<ReviewRunDto>.Ok(query.ToDto(winner), CollectionsOutcome.Replayed);
        }
        scheduler.EnqueueRefresh(run.CollectionsReviewRunId);
        return CollectionsResult<ReviewRunDto>.Ok(query.ToDto(run), CollectionsOutcome.Accepted);
    }

    private bool IsDead(CollectionsReviewRun run, DateTime now) =>
        now - (run.StartedAtUtc ?? run.RequestedAtUtc) > TimeSpan.FromMinutes(Math.Max(1, reviewOptions.StuckRunMinutes));

    /// <summary>The background job body. Idempotent: a second execution of an already-claimed run does nothing.</summary>
    public async Task RunAsync(long runId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        if (!await store.TryStartRunAsync(runId, now, ct)) { logger.LogInformation("Review refresh {RunId} was already claimed or finished.", runId); return; }
        var run = await store.GetRunAsync(runId, ct);
        if (run is null) return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, reviewOptions.RefreshTimeoutMinutes)));
        try
        {
            await store.UpdateRunProgressAsync(runId, "Reading the financial source", 5, null, ct);
            var snapshot = await source.ReadAsync(new PactReceivablesRequest(run.DueFrom, run.DueTo, run.CompanyId, MinAmount: decimal.Round(sourceOptions.DefaultMinOutstandingAmount, 4)), budget.Token);
            // Same minimum outstanding amount as the Campaigns preview. A local snapshot that is not loaded or does not cover the window would silently omit
            // receivables: refuse. (A merely stale snapshot is flagged on every record through its read time, as before.)
            if (snapshot.Snapshot is { } snapshotStatus && (!snapshotStatus.RangeCovered || snapshotStatus.Companies.Any(c => !c.HasSnapshot)))
                throw new PactReceivablesSourceException($"Receivables data is not ready. {snapshotStatus.ReadyProblem}");
            await store.UpdateRunProgressAsync(runId, "Validating records", 45, snapshot.Items.Count, ct);
            if (snapshot.Items.Any(r => r.CompanyId is not (4 or 32)))
                throw new PactReceivablesSourceException("PACT returned a receivable without a valid company identity.");

            var built = ReviewRecordBuilder.Build(snapshot.Items, Context(run.AsOfDate, snapshot.ReadAtUtc));
            logger.LogInformation("Review refresh {RunId}: {Rows} source rows, {Records} records, {Noise} float-noise amounts normalized, {Settled} settled rows ignored.",
                runId, snapshot.Items.Count, built.Records.Count, built.FloatNormalizedRows, built.SettledRows);

            await store.UpdateRunProgressAsync(runId, "Saving records", 60, snapshot.Items.Count, ct);
            const int chunk = 2000;
            for (var i = 0; i < built.Records.Count; i += chunk)
            {
                await store.AddRecordsAsync(runId, built.Records.Skip(i).Take(chunk).ToList(), budget.Token);
                var percent = 60 + (int)(35.0 * Math.Min(built.Records.Count, i + chunk) / Math.Max(1, built.Records.Count));
                await store.UpdateRunProgressAsync(runId, "Saving records", percent, snapshot.Items.Count, ct);
            }
            await store.PublishRunAsync(runId, built.Records.Count, snapshot.Items.Count, snapshot.ReadAtUtc, clock.UtcNow, reviewOptions.RunsToKeep, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await store.FailRunAsync(runId, "The refresh was cancelled.", clock.UtcNow, CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException)
        {
            await store.FailRunAsync(runId, "The financial source read exceeded the time budget. Retry, or refresh one company.", clock.UtcNow, CancellationToken.None);
        }
        catch (PactReceivablesSourceException ex)
        {
            await store.FailRunAsync(runId, ex.Message, clock.UtcNow, CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbException or InvalidCastException or FormatException or OverflowException)
        {
            logger.LogWarning("Review refresh {RunId} failed ({ExceptionType}).", runId, ex.GetType().Name);
            await store.FailRunAsync(runId, "The financial source could not be read.", clock.UtcNow, CancellationToken.None);
        }
    }
}

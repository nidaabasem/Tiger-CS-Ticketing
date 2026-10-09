using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

/// <summary>
/// Re-reads current balances for the companies involved in a dispatch or a suppression sweep and rebuilds the review records, keyed by
/// <c>RecordKey</c>. One read per company, run concurrently (they hit different PACT databases).
///
/// <para><b>Source limitation.</b> The PACT report procedures accept only <c>@StartDate</c>, <c>@EndDate</c> and <c>@MinAmount</c> (V2 adds
/// <c>@IncludeSettled</c> and <c>@StrictIdentity</c>); none selects a customer, unit or tag, and the ledger and allocation work runs in full whatever the
/// window. The window cannot be narrowed safely either: a unit's quoted amount sums every qualifying instalment from the configured start date, and an
/// older instalment that becomes unpaid again (a reversed cheque) would be missed. So the cost of one revalidation is one full company read, which is
/// why it runs in the background job with a lease heartbeat. Targeted reads need a new, separately reviewed procedure parameter; none is invented here.</para>
/// </summary>
public sealed class CurrentBalanceReader(PactReceivablesOptions sourceOptions, IPactReceivablesSource source, ReviewRefreshService refresh,
    ILogger<CurrentBalanceReader> logger)
{
    public sealed record Result(IReadOnlyDictionary<DateOnly, IReadOnlyDictionary<string, CollectionsReviewRecord>> ByAsOfDate, long ElapsedMs, int SourceRows);

    public async Task<Result> ReadAsync(IReadOnlyCollection<int> companies, IReadOnlyCollection<DateOnly> asOfDates, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var latest = asOfDates.Max();
        var from = DateOnly.FromDateTime(sourceOptions.StartDate);
        var to = new DateOnly(latest.Year, latest.Month, DateTime.DaysInMonth(latest.Year, latest.Month));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(sourceOptions.RequestBudgetSeconds));

        var snapshots = await Task.WhenAll(companies.Distinct().Select(company =>
            source.ReadAsync(new PactReceivablesRequest(from, to, company), budget.Token)));

        var byDate = new Dictionary<DateOnly, IReadOnlyDictionary<string, CollectionsReviewRecord>>();
        foreach (var asOf in asOfDates.Distinct())
        {
            var map = new Dictionary<string, CollectionsReviewRecord>(StringComparer.Ordinal);
            foreach (var snapshot in snapshots)
                foreach (var record in ReviewRecordBuilder.Build(snapshot.Items, refresh.Context(asOf, snapshot.ReadAtUtc)).Records)
                    map[record.RecordKey] = record;
            byDate[asOf] = map;
        }
        var rows = snapshots.Sum(s => s.Items.Count);
        var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        logger.LogInformation("Current balances read for {Companies} company(ies): {Rows} source rows in {ElapsedMs} ms.", companies.Count, rows, elapsed);
        return new Result(byDate, elapsed, rows);
    }
}

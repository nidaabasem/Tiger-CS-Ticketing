using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Modules.Collections;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Rcv;

/// <summary>
/// Timings of the page and campaign reads over an already loaded, full-volume snapshot (the local fake PACT at ~300 k / ~144 k rows). Runs only when TIGERCS_PERF_SQL points at that database AND
/// TIGERCS_PERF_TIMINGS is set; writes a small table to the file named by TIGERCS_PERF_TIMINGS. Not an assertion of a production number: the server, PACT and volumes differ.
/// </summary>
[Collection("RcvRealSql")]
public sealed class RcvFullVolumeTimings
{
    [SqlFact("TIGERCS_PERF_TIMINGS")]
    public async Task ReadTimingsOverTheFullWindow()
    {
        var connectionString = Environment.GetEnvironmentVariable("TIGERCS_PERF_SQL"); var output = Environment.GetEnvironmentVariable("TIGERCS_PERF_TIMINGS");
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(output)) return;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TigerCsDatabase"] = connectionString }).Build();
        var source = new RcvReceivablesSource(config, new ReceivablesSnapshotOptions(), new PactReceivablesOptions { Enabled = true, MaxSourceRows = 1_000_000 }, TimeProvider.System, NullLogger<RcvReceivablesSource>.Instance);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(4));
        var lines = new List<string>();
        async Task<T> Time<T>(string label, Func<Task<T>> run, Func<T, string> describe)
        {
            await run();                                                                                      // warm-up (plan cache)
            var t = Stopwatch.StartNew(); var value = await run(); t.Stop();
            lines.Add($"{label,-62} {t.ElapsedMilliseconds,6} ms   {describe(value)}");
            return value;
        }
        PactInstalmentsRequest Req(decimal? min, string? status = null, string? search = null, int page = 1, int? company = null, DateOnly? from = null) =>
            new(from ?? new DateOnly(2000, 1, 1), today, today, 0m, "outstanding", search, null, page, 25, company, null, status, true, min);
        await Time("Receivables page 1, All months, Minimum Total 100", () => source.ReadInstalmentUnitsAsync(Req(100m), default), p => $"{p.Totals.UnitCount:N0} units, {p.Totals.Count:N0} payments, Total {p.Totals.RemainingTotal:N0}");
        await Time("Receivables page 40, same filters", () => source.ReadInstalmentUnitsAsync(Req(100m, page: 40), default), p => $"{p.Units.Count} units on the page");
        await Time("Receivables, Minimum Total cleared", () => source.ReadInstalmentUnitsAsync(Req(null), default), p => $"{p.Totals.UnitCount:N0} units");
        await Time("Receivables, status Overdue only", () => source.ReadInstalmentUnitsAsync(Req(100m, "overdue"), default), p => $"{p.Totals.UnitCount:N0} units");
        await Time("Receivables, search 'Customer 12'", () => source.ReadInstalmentUnitsAsync(Req(100m, search: "Customer 12"), default), p => $"{p.Totals.UnitCount:N0} units");
        await Time("Receivables, Dubai only", () => source.ReadInstalmentUnitsAsync(Req(100m, company: 4), default), p => $"{p.Totals.UnitCount:N0} units");
        await Time("Receivables, Sharjah only", () => source.ReadInstalmentUnitsAsync(Req(100m, company: 32), default), p => $"{p.Totals.UnitCount:N0} units");
        await Time("Receivables, one month (2026-09)", () => source.ReadInstalmentUnitsAsync(Req(100m, from: new DateOnly(2026, 9, 1)) with { To = new DateOnly(2026, 9, 30) }, default), p => $"{p.Totals.UnitCount:N0} units");
        var rows = await Time("Campaign/Review payment rows, 2026-01-01..today", () => source.ReadAsync(new PactReceivablesRequest(new DateOnly(2026, 1, 1), today), default), r => $"{r.Items.Count:N0} rows");

        var options = new CollectionsOptions { Enabled = true };
        var service = new CollectionsCampaignAppService(options, new CollectionsCampaignOptions { FinancialSourceValidated = true }, new PactReceivablesOptions { Enabled = true, DefaultMinOutstandingAmount = 0m },
            new(options, new FakeDepartmentRepository()), new(options, new FakeTimeProvider(DateTime.UtcNow)), source, NullLogger<CollectionsCampaignAppService>.Instance);
        var manager = new CollectionsCaller(Guid.NewGuid(), [Roles.CsManager], []);
        await Time("Campaigns preview, OverdueReminder, page 1 (service)", async () => (await service.PreviewAsync(manager, "OverdueReminder", minTotal: 100m)).Value!, p => $"{p.TotalCount:N0} units");
        await Time("Campaigns review export, OverdueReminder (service)", async () => await service.ExportAsync(manager, "OverdueReminder", "review", minTotal: 100m), e => e.IsSuccess ? $"{e.Value!.RowCount:N0} rows" : e.Outcome + " " + e.Detail);
        lines.Add($"(loaded payments in memory for the campaign evaluation: {rows.Items.Count:N0})");
        await File.WriteAllLinesAsync(output, lines);
    }
}

using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Modules.Collections;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Perf;

/// <summary>
/// RESULT EQUIVALENCE of the Campaigns SQL engine (dbo.usp_Collections_GetCampaignUnits) against the application's in-memory evaluation, on a REAL SQL Server
/// holding the snapshot (database/collections-receivables V001-V007 + the synthetic PACT data and the hand-written edge cases of tests/local-sqlserver).
/// For every scenario the SAME service is built twice over the SAME snapshot - once with the SQL engine, once with the engine hidden so the original
/// in-memory path runs - and the preview (items, totals, status/reason strings, order, paging) and both CSV exports must be identical.
/// They run only when TIGERCS_PERF_SQL holds a connection string (never in CI); otherwise they return immediately.
/// </summary>
public sealed class RealSqlCampaignEquivalenceTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TIGERCS_PERF_SQL");
    private static readonly CollectionsCaller Manager = new(Guid.NewGuid(), [Roles.CsManager], []);

    /// <summary>Hides IPactCampaignSource: the service then reads every instalment of the window and evaluates in memory (the reference implementation).
    /// The window read itself is cached per request (the snapshot does not change while the tests run), so only the evaluation is repeated.</summary>
    private sealed class RowsOnly(IPactReceivablesSource inner) : IPactReceivablesSource
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<PactReceivablesRequest, Task<PactReceivablesSnapshot>> Cache = new();
        public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) => inner.ReadAsync(throughDate, cancellationToken);
        public Task<PactReceivablesSnapshot> ReadAsync(PactReceivablesRequest request, CancellationToken cancellationToken) =>
            Environment.GetEnvironmentVariable("TIGERCS_PERF_TIMINGS") == "1" ? inner.ReadAsync(request, cancellationToken)
                : Cache.GetOrAdd(request, r => inner.ReadAsync(r, CancellationToken.None));
    }

    private static SnapshotPactReceivablesSource NewSource()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TigerCsDatabase"] = ConnectionString }).Build();
        return new SnapshotPactReceivablesSource(config, new ReceivablesSnapshotOptions { MaxAgeMinutes = 100000 }, new PactReceivablesOptions { Enabled = true },
            TimeProvider.System, NullLogger<SnapshotPactReceivablesSource>.Instance);
    }

    private static CollectionsCampaignAppService Service(IPactReceivablesSource source, DateTime nowUtc, bool legalNoticeRelease = false)
    {
        var options = new CollectionsOptions { Enabled = true };
        return new(options, new CollectionsCampaignOptions { FinancialSourceValidated = true, LegalNoticeExportEnabled = legalNoticeRelease }, new PactReceivablesOptions { Enabled = true },
            new(options, new FakeDepartmentRepository()), new(options, new FakeTimeProvider(nowUtc)), source, NullLogger<CollectionsCampaignAppService>.Instance);
    }

    private static async Task<int?> TowerIdAsync(string number)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT TOP (1) TowerId FROM dbo.CollectionsTowers WHERE LTRIM(RTRIM(CONVERT(nvarchar(20), TowerNumber))) = @n AND IsActive = 1", connection);
        command.Parameters.AddWithValue("@n", number);
        return await command.ExecuteScalarAsync() is int id ? id : null;
    }

    // Preview dates that are scheduled for each stage (so units can be "Ready"), plus the clock "now" (09:00 Dubai) that makes them the live business date.
    private static (DateOnly Date, DateTime NowUtc) Scheduled(string stage) => stage switch
    {
        "OverdueReminder" => (new(2026, 10, 1), new(2026, 10, 1, 5, 0, 0, DateTimeKind.Utc)),
        "CurrentMonthReminder" => (new(2026, 10, 14), new(2026, 10, 14, 5, 0, 0, DateTimeKind.Utc)),
        "FollowUpReminder" => (new(2026, 10, 28), new(2026, 10, 28, 5, 0, 0, DateTimeKind.Utc)),
        "LegalNotice" => (new(2026, 10, 12), new(2026, 10, 12, 5, 0, 0, DateTimeKind.Utc)),
        _ => (new(2026, 10, 30), new(2026, 10, 30, 5, 0, 0, DateTimeKind.Utc))
    };

    private static readonly string[] Stages = ["OverdueReminder", "CurrentMonthReminder", "FollowUpReminder", "LegalNotice", "LegalReferral"];

    public static IEnumerable<object?[]> Scenarios()
    {
        // stage, tower number, company, from, to, min, search, release of client-facing legal notices
        foreach (var stage in Stages)
        {
            yield return [stage, null, null, null, null, 100m, null, false];
            yield return [stage, null, null, null, null, 0m, null, true];
            yield return [stage, null, null, null, null, 1000m, null, false];
            yield return [stage, null, 4, null, null, 100m, null, false];
            yield return [stage, null, 32, null, null, 100m, null, false];
            yield return [stage, "103", null, null, null, 100m, null, true];
            yield return [stage, "127", null, null, null, 0m, null, false];
            yield return [stage, null, null, "2026-03-01", "2026-03-31", 100m, null, false];
            yield return [stage, null, null, "2025-04-09", "2026-10-31", 99.99m, null, false];
            yield return [stage, null, null, null, null, 100m, "EDGE", false];
            yield return [stage, null, null, "2026-01-01", "2026-12-31", 0m, "Customer 12", false];
            yield return [stage, null, null, null, null, 100m, "+9715011100", false];
            yield return [stage, null, null, null, null, 100m, "o_brien", false];
            yield return [stage, null, null, null, null, 100m, "100%", false];
            yield return [stage, null, null, null, null, 100m, "[Test]", false];
            yield return [stage, null, null, null, null, 100m, "عميل", false];
            yield return [stage, null, null, null, null, 100m, "jane roe", false];
            yield return [stage, null, null, null, null, 100m, "TP106-", false];
        }
    }

    private static string Describe(object?[] s) => string.Join(" | ", s.Select(x => x?.ToString() ?? "-"));

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Preview_Totals_AndBothExports_AreIdentical_ToTheInMemoryEvaluation(
        string stage, string? towerNumber, int? company, string? from, string? to, decimal min, string? search, bool release)
    {
        if (string.IsNullOrEmpty(ConnectionString)) return;
        var (date, nowUtc) = Scheduled(stage);
        var source = NewSource();
        var sql = Service(source, nowUtc, release);
        var memory = Service(new RowsOnly(source), nowUtc, release);
        int? towerId = towerNumber is null ? null : await TowerIdAsync(towerNumber);
        DateOnly? f = from is null ? null : DateOnly.Parse(from), t = to is null ? null : DateOnly.Parse(to);
        var label = Describe([stage, towerNumber, company, from, to, min, search, release]);

        // First, last and a middle page: paging happens after eligibility is decided, so every page must agree.
        var first = await memory.PreviewAsync(Manager, stage, date, company, search, 1, 25, false, default, f, t, towerId, min);
        Assert.True(first.IsSuccess, label + ": " + first.Detail);
        var pages = Math.Max(1, (first.Value!.TotalCount + 24) / 25);
        foreach (var page in new[] { 1, Math.Min(2, pages), pages }.Distinct())
        {
            var a = await sql.PreviewAsync(Manager, stage, date, company, search, page, 25, false, default, f, t, towerId, min);
            var b = page == 1 ? first : await memory.PreviewAsync(Manager, stage, date, company, search, page, 25, false, default, f, t, towerId, min);
            Assert.True(a.IsSuccess, label + ": " + a.Detail);
            var x = a.Value!; var y = b.Value!;
            Assert.Equal((y.TotalCount, y.ReadyCount, y.ReviewCount), (x.TotalCount, x.ReadyCount, x.ReviewCount));
            Assert.Equal(y.Items, x.Items);                                                      // every field of every row, in order (record equality)
            Assert.Equal((y.Stage, y.CycleKey, y.IsScheduledDate, y.DateFrom, y.DateTo, y.MinAmount, y.TowerId, y.ReadAtUtc),
                         (x.Stage, x.CycleKey, x.IsScheduledDate, x.DateFrom, x.DateTo, x.MinAmount, x.TowerId, x.ReadAtUtc));
            Assert.Equal(y.RangeNotes, x.RangeNotes);
            Assert.Equal(y.Snapshot!.IsReady, x.Snapshot!.IsReady);
        }

        // Both CSV exports: byte-identical, or refused identically (over the row limit / not every row ready).
        foreach (var mode in new[] { "review", "genesys" })
        {
            var a = await sql.ExportAsync(Manager, stage, mode, date, company, search, default, f, t, towerId, min);
            var b = await memory.ExportAsync(Manager, stage, mode, date, company, search, default, f, t, towerId, min);
            Assert.Equal((b.IsSuccess, b.Outcome, b.Detail), (a.IsSuccess, a.Outcome, a.Detail));
            if (b.IsSuccess)
            {
                Assert.Equal(b.Value!.Csv, a.Value!.Csv);
                Assert.Equal((b.Value.FileName, b.Value.RowCount), (a.Value.FileName, a.Value.RowCount));
            }
        }
    }

    [Fact]
    public async Task TheSqlEngine_IsUsed_AndEveryReviewFlagOfTheEdgeDataIsExercised()
    {
        if (string.IsNullOrEmpty(ConnectionString)) return;
        var source = NewSource();
        var seen = CollectionsCampaignFlags.None;
        foreach (var stage in Stages)
        {
            var (date, _) = Scheduled(stage);
            var (from, toExclusive) = CollectionsCampaignPolicy.StageRange(Enum.Parse<CollectionsCampaignStage>(stage), date);
            var page = await source.ReadCampaignAsync(new PactCampaignRequest(new(date.Year, 1, 1), CollectionsCampaignPolicy.DefaultDateTo(Enum.Parse<CollectionsCampaignStage>(stage), date),
                0m, from, toExclusive, CollectionsCampaignPolicy.Threshold(Enum.Parse<CollectionsCampaignStage>(stage)), stage != "LegalReferral", "EDGE", null, 0, 500, null, null), default);
            Assert.True(page.Supported, "the snapshot must be prepared for the SQL engine (publish V004 with V007)");
            foreach (var unit in page.Units) seen |= (CollectionsCampaignFlags)unit.Flags;
        }
        // The edge data (tests/local-sqlserver/03_edge_cases.sql) holds an example of every unit-level reason.
        foreach (var flag in Enum.GetValues<CollectionsCampaignFlags>().Where(v => v != CollectionsCampaignFlags.None))
            Assert.True((seen & flag) != 0, $"no edge case exercised {flag}; load tests/local-sqlserver/03_edge_cases.sql and refresh the snapshot");
    }

    [Fact]
    public async Task ASnapshotTheEngineCannotUse_FallsBackToTheInMemoryEvaluation_NeverToAnEmptyList()
    {
        if (string.IsNullOrEmpty(ConnectionString)) return;
        var (date, nowUtc) = Scheduled("OverdueReminder");
        var source = NewSource();
        var sql = Service(source, nowUtc);
        var memory = Service(new RowsOnly(source), nowUtc);
        var expected = await memory.PreviewAsync(Manager, "OverdueReminder", date, 4, "EDGE", 1, 25, false, default, null, null, null, 100m);
        Assert.True(expected.IsSuccess); Assert.True(expected.Value!.TotalCount > 0);
        foreach (var marker in new object[] { DBNull.Value, 3 })   // NULL = published before V007; > 0 = text .NET trims but T-SQL cannot
        {
            await SetExoticAsync(4, marker);
            try
            {
                var page = await source.ReadCampaignAsync(new PactCampaignRequest(new(2026, 1, 1), date, 100m, null, new(2026, 9, 9), 0m, true, "EDGE", null, 0, 25, 4, null), default);
                Assert.False(page.Supported);                                              // the engine reports it cannot answer ...
                var viaService = await sql.PreviewAsync(Manager, "OverdueReminder", date, 4, "EDGE", 1, 25, false, default, null, null, null, 100m);
                Assert.True(viaService.IsSuccess);
                Assert.Equal(expected.Value.Items, viaService.Value!.Items);               // ... and the service still returns the complete, correct list
                Assert.Equal(expected.Value.TotalCount, viaService.Value.TotalCount);
            }
            finally { await SetExoticAsync(4, 0); }
        }
    }

    [Theory]
    [InlineData("everything", "UPDATE dbo.CollectionsContactNorm SET Norm = NULL, NormVersion = NULL; UPDATE dbo.CollectionsReceivableUnit SET PhoneOk = NULL, EmailOk = NULL, PhoneVer = NULL, EmailVer = NULL WHERE PhoneId IS NOT NULL OR EmailId IS NOT NULL")]
    [InlineData("units only", "UPDATE dbo.CollectionsReceivableUnit SET PhoneOk = NULL, EmailOk = NULL, PhoneVer = NULL, EmailVer = NULL WHERE PhoneId IS NOT NULL OR EmailId IS NOT NULL")]
    [InlineData("older version", "UPDATE dbo.CollectionsContactNorm SET NormVersion = 0; UPDATE dbo.CollectionsReceivableUnit SET PhoneVer = 0, EmailVer = 0 WHERE PhoneId IS NOT NULL OR EmailId IS NOT NULL")]
    public async Task ContactValuesNotNormalisedYet_AreFilledOnTheFirstRead_WithoutChangingTheResult(string what, string sabotage)
    {
        if (string.IsNullOrEmpty(ConnectionString)) return;
        var (date, nowUtc) = Scheduled("OverdueReminder");
        var source = NewSource();
        var sql = Service(source, nowUtc);
        var memory = Service(new RowsOnly(source), nowUtc);
        var expected = await memory.PreviewAsync(Manager, "OverdueReminder", date, null, "EDGE", 1, 100, false, default, null, null, null, 0m);
        Assert.True(expected.IsSuccess);
        await using (var connection = new SqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(sabotage, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync();
        }
        // The very first read after a publish (or a rule change) normalises what is missing, records it and answers - never an empty or partial list.
        var actual = await sql.PreviewAsync(Manager, "OverdueReminder", date, null, "EDGE", 1, 100, false, default, null, null, null, 0m);
        Assert.True(actual.IsSuccess, what + ": " + actual.Detail);
        Assert.Equal(expected.Value!.Items, actual.Value!.Items);
        Assert.Equal((expected.Value.TotalCount, expected.Value.ReadyCount, expected.Value.ReviewCount), (actual.Value.TotalCount, actual.Value.ReadyCount, actual.Value.ReviewCount));
        // A second read needs no normalisation any more and gives the same answer.
        var again = await sql.PreviewAsync(Manager, "OverdueReminder", date, null, "EDGE", 1, 100, false, default, null, null, null, 0m);
        Assert.Equal(actual.Value.Items, again.Value!.Items);
    }

    private static async Task SetExoticAsync(int company, object value)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("UPDATE dbo.CollectionsReceivableCompanyState SET ExoticTextRows = @v WHERE CompanyId = @c", connection);
        command.Parameters.AddWithValue("@v", value); command.Parameters.AddWithValue("@c", company);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ContactValuesNormalisedByTheApplication_AreExactlyTheStoredOnes()
    {
        if (string.IsNullOrEmpty(ConnectionString)) return;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT Kind, Raw, Norm FROM dbo.CollectionsContactNorm WHERE NormVersion = @v", connection);
        command.Parameters.AddWithValue("@v", (byte)CollectionsContactNormalizer.Version);
        var count = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var raw = reader.GetString(1);
            var expected = reader.GetByte(0) == 1 ? CollectionsContactNormalizer.NormalizePhone(raw) : CollectionsContactNormalizer.NormalizeEmail(raw);
            Assert.Equal(expected, reader.GetString(2));
            count++;
        }
        Assert.True(count > 0);
    }

    // ---------------------------------------------------------------- latency idle and during a refresh (TIGERCS_PERF_LATENCY=sql|memory)
    private static double Percentile(List<double> values, double p) { var s = values.OrderBy(x => x).ToList(); return s[Math.Min(s.Count - 1, (int)Math.Round(p / 100 * (s.Count - 1)))]; }

    private static long PeakWorkingSetMb()
    {
        foreach (var line in File.ReadLines("/proc/self/status")) if (line.StartsWith("VmHWM:", StringComparison.Ordinal)) return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) / 1024;
        return 0;
    }

    [Fact]
    public async Task Latency_IdleAndDuringARefresh_ForTheSelectedEvaluationPath()
    {
        var path = Environment.GetEnvironmentVariable("TIGERCS_PERF_LATENCY");
        if (string.IsNullOrEmpty(ConnectionString) || path is not ("sql" or "memory")) return;
        var (date, nowUtc) = Scheduled("OverdueReminder");
        var source = NewSource();
        var service = path == "sql" ? Service(source, nowUtc) : Service(new RowsOnlyUncached(source), nowUtc);
        var lines = new List<string>();
        async Task<double> One()
        {
            var sw = Stopwatch.StartNew();
            var r = await service.PreviewAsync(Manager, "OverdueReminder", date, null, null, 1, 25, false, default, null, null, null, 100m);
            sw.Stop();
            Assert.True(r.IsSuccess, r.Detail);
            return sw.Elapsed.TotalMilliseconds;
        }
        await One(); await One();                                                              // plan cache / JIT warm-up
        var idle = new List<double>(); long alloc = 0;
        var idleCalls = path == "sql" ? 60 : 12;
        for (var i = 0; i < idleCalls; i++) { var before = GC.GetTotalAllocatedBytes(true); idle.Add(await One()); alloc = Math.Max(alloc, GC.GetTotalAllocatedBytes(true) - before); }
        lines.Add($"{path} idle: n={idle.Count} p50={Percentile(idle, 50):F0}ms p95={Percentile(idle, 95):F0}ms max={idle.Max():F0}ms | allocated/request (max)={alloc / 1_000_000.0:F1} MB | peak working set so far={PeakWorkingSetMb()} MB");

        // The same previews while the real refresh procedure publishes a new run on another connection.
        var busy = new List<double>();
        var refresh = Task.Run(async () =>
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand("EXEC dbo.usp_Collections_RefreshReceivables @TriggerSource = N'latency-test'", connection) { CommandTimeout = 1200 };
            await command.ExecuteNonQueryAsync();
        });
        await Task.Delay(2000);
        var t0 = Stopwatch.StartNew();
        while (!refresh.IsCompleted && t0.Elapsed < TimeSpan.FromMinutes(6)) busy.Add(await One());
        await refresh;
        lines.Add($"{path} during refresh: n={busy.Count} p50={Percentile(busy, 50):F0}ms p95={Percentile(busy, 95):F0}ms max={busy.Max():F0}ms | refresh ran {t0.Elapsed.TotalSeconds:F0} s | peak working set={PeakWorkingSetMb()} MB");
        File.WriteAllLines($"/tmp/claude-0/perf/campaign_latency_{path}.txt", lines);
    }

    private sealed class RowsOnlyUncached(IPactReceivablesSource inner) : IPactReceivablesSource
    {
        public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) => inner.ReadAsync(throughDate, cancellationToken);
        public Task<PactReceivablesSnapshot> ReadAsync(PactReceivablesRequest request, CancellationToken cancellationToken) => inner.ReadAsync(request, cancellationToken);
    }

    // ---------------------------------------------------------------- performance (synthetic data; writes /tmp/claude-0/perf/campaign_csharp.txt)
    [Fact]
    public async Task Timings_SqlEngineVersusInMemory_OnTheSyntheticSnapshot()
    {
        if (string.IsNullOrEmpty(ConnectionString) || Environment.GetEnvironmentVariable("TIGERCS_PERF_TIMINGS") != "1") return;
        var lines = new List<string>();
        var source = NewSource();
        foreach (var (name, stage, towerNumber, from, to, min, search) in new (string, string, string?, string?, string?, decimal, string?)[]
        {
            ("Overdue, default window, all towers", "OverdueReminder", null, null, null, 100m, null),
            ("Overdue, wide window 2025-04-09..2026-10-31", "OverdueReminder", null, "2025-04-09", "2026-10-31", 100m, null),
            ("Current month, default window", "CurrentMonthReminder", null, null, null, 100m, null),
            ("Overdue, tower 103", "OverdueReminder", "103", null, null, 100m, null),
            ("Overdue, search 'Customer 1234'", "OverdueReminder", null, null, null, 100m, "Customer 1234"),
            ("Legal notice, default window", "LegalNotice", null, null, null, 100m, null)
        })
        {
            var (date, nowUtc) = Scheduled(stage);
            int? towerId = towerNumber is null ? null : await TowerIdAsync(towerNumber);
            DateOnly? f = from is null ? null : DateOnly.Parse(from), t = to is null ? null : DateOnly.Parse(to);
            foreach (var (path, svc) in new[] { ("SQL engine (new)", Service(source, nowUtc)), ("in memory (before)", Service(new RowsOnly(source), nowUtc)) })
            {
                var times = new List<double>(); long alloc = 0; var units = 0;
                for (var i = 0; i < 12; i++)
                {
                    var before = GC.GetTotalAllocatedBytes(true);
                    var sw = Stopwatch.StartNew();
                    var r = await svc.PreviewAsync(Manager, stage, date, null, search, 1, 25, false, default, f, t, towerId, min);
                    sw.Stop();
                    Assert.True(r.IsSuccess, r.Detail);
                    times.Add(sw.Elapsed.TotalMilliseconds); alloc = GC.GetTotalAllocatedBytes(true) - before; units = r.Value!.TotalCount;
                }
                var warm = times.Skip(1).OrderBy(x => x).ToList();
                lines.Add($"{name} | {path} | units={units} | first={times[0]:F0}ms | warm median={warm[warm.Count / 2]:F0}ms | warm max={warm[^1]:F0}ms | allocated/request={alloc / 1_000_000.0:F1} MB");
            }
        }
        File.WriteAllLines("/tmp/claude-0/perf/campaign_csharp.txt", lines);
    }
}

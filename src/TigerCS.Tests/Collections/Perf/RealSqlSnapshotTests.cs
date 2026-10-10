using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Tests.Collections.Snapshot;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Modules.Collections;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Perf;

/// <summary>
/// Tests against a REAL SQL Server holding the deployed snapshot objects (database/collections-receivables V001-V006 plus synthetic PACT data).
/// They run only when TIGERCS_PERF_SQL holds a connection string (never in CI); otherwise they return immediately. Used to prove that the
/// SQL-side aggregation and paging gives the same answer as the application-side rules and to take timings.
/// </summary>
public sealed class RealSqlSnapshotTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TIGERCS_PERF_SQL");

    /// <summary>Hides IPactReceivablesPageSource so the service takes its original in-memory path over the instalment rows.</summary>
    private sealed class RowsOnly(IPactReceivablesSource inner) : IPactReceivablesSource
    {
        public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) => inner.ReadAsync(throughDate, cancellationToken);
        public Task<PactReceivablesSnapshot> ReadAsync(PactReceivablesRequest request, CancellationToken cancellationToken) => inner.ReadAsync(request, cancellationToken);
    }

    private static (PactReceivableCustomersAppService Sql, PactReceivableCustomersAppService InMemory, SnapshotPactReceivablesSource Source) Build()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TigerCsDatabase"] = ConnectionString }).Build();
        var snapshotOptions = new ReceivablesSnapshotOptions { MaxAgeMinutes = 100000 };
        var pact = new PactReceivablesOptions { Enabled = true };
        var source = new SnapshotPactReceivablesSource(config, snapshotOptions, pact, TimeProvider.System, NullLogger<SnapshotPactReceivablesSource>.Instance);
        var options = new CollectionsOptions { Enabled = true };
        PactReceivableCustomersAppService Make(IPactReceivablesSource s) => new(options, pact, new CollectionsAuthorizationService(options, new FakeDepartmentRepository()),
            new CollectionsClock(options, new FakeTimeProvider(new DateTime(2026, 10, 9, 5, 0, 0, DateTimeKind.Utc))), s, NullLogger<PactReceivableCustomersAppService>.Instance);
        return (Make(source), Make(new RowsOnly(source)), source);
    }

    private static readonly CollectionsCaller Caller = new(Guid.NewGuid(), [Roles.CsAgent], []);

    public static IEnumerable<object?[]> Scenarios()
    {
        // tower, from, to, status, search, minAmount, page
        yield return [null, null, null, null, null, 100m, 1];
        yield return [null, null, null, null, null, 0m, 1];
        yield return [null, null, null, null, null, 1000m, 1];
        yield return [null, null, null, null, null, 100m, 7];
        yield return [5, null, null, null, null, 100m, 1];
        yield return [5, null, null, "overdue", null, 0m, 2];
        yield return [null, null, null, "due", null, 100m, 3];
        yield return [null, "2025-04-09", "2026-10-31", "overdue", null, 100m, 1];
        yield return [null, "2026-01-01", "2026-03-31", null, null, 250.5m, 1];
        yield return [31, null, null, null, null, 100m, 1];                                // a Sharjah tower
        yield return [null, null, null, null, "Customer 1234", 100m, 1];
        yield return [null, null, null, null, "+97150000", 100m, 1];                        // phone-like search
        yield return [null, null, null, null, "TP110-", 100m, 1];                            // unit-code search
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task SqlAggregationAndPaging_GiveTheSameAnswerAsTheApplicationSideRules(int? tower, string? from, string? to, string? status, string? search, decimal min, int page)
    {
        if (string.IsNullOrEmpty(ConnectionString)) return;
        var (sql, memory, _) = Build();
        var f = from is null ? (DateOnly?)null : DateOnly.Parse(from); var t = to is null ? (DateOnly?)null : DateOnly.Parse(to);
        var a = await sql.ListAsync(Caller, towerId: tower, dateFrom: f, dateTo: t, status: status, search: search, minAmount: min, page: page);
        var b = await memory.ListAsync(Caller, towerId: tower, dateFrom: f, dateTo: t, status: status, search: search, minAmount: min, page: page);
        Assert.True(a.IsSuccess, a.Detail); Assert.True(b.IsSuccess, b.Detail);
        var x = a.Value!; var y = b.Value!;
        Assert.Equal((y.TotalCount, y.DueCustomerCount, y.OverdueCustomerCount), (x.TotalCount, x.DueCustomerCount, x.OverdueCustomerCount));
        Assert.Equal(y.Items.Select(c => (c.CompanyId, c.TenantId, c.UnitId, c.UnitCode)), x.Items.Select(c => (c.CompanyId, c.TenantId, c.UnitId, c.UnitCode)));
        for (var i = 0; i < y.Items.Count; i++)
        {
            var m = y.Items[i]; var s = x.Items[i];
            Assert.Equal((m.DueAmount, m.OverdueAmount, m.TotalAmount, m.AmountStatus, m.HasDue, m.HasOverdue, m.EarliestDueDate, m.OverdueDays, m.TowerNumber),
                         (s.DueAmount, s.OverdueAmount, s.TotalAmount, s.AmountStatus, s.HasDue, s.HasOverdue, s.EarliestDueDate, s.OverdueDays, s.TowerNumber));
            Assert.Equal(m.Instalments.Select(r => (r.DueDate, r.RemainingAmount, r.ReceivablesType, r.VoucherNumber)).OrderBy(r => r.DueDate).ThenBy(r => r.VoucherNumber),
                         s.Instalments.Select(r => (r.DueDate, r.RemainingAmount, r.ReceivablesType, r.VoucherNumber)).OrderBy(r => r.DueDate).ThenBy(r => r.VoucherNumber));
            Assert.All(s.Instalments, r => Assert.True(r.RemainingAmount >= min));            // the minimum is inclusive and applies to every listed instalment
        }
        if (min == 0m && status is null && search is null && tower is null) Assert.True(x.TotalCount > 0);
    }

    [Fact]
    public async Task MinimumAmountIsInclusive_AndLoweringItNeedsNoReload()
    {
        if (string.IsNullOrEmpty(ConnectionString)) return;
        var (sql, _, source) = Build();
        Task<PactInstalmentsPage> Read(decimal min) => source.ReadInstalmentsAsync(new PactInstalmentsRequest(new(2026, 1, 1), new(2026, 10, 31), new(2026, 10, 1), min, "outstanding", null, null, 1, 25, null, null), default);
        var at0 = (await Read(0m)).Totals; var at100 = (await Read(100m)).Totals; var at1000 = (await Read(1000m)).Totals;
        Assert.True(at0.Count >= at100.Count && at100.Count > at1000.Count, "raising the minimum can only remove instalments");
        Assert.True(at0.RemainingTotal >= at100.RemainingTotal && at100.RemainingTotal > at1000.RemainingTotal);   // totals follow the same threshold as the rows
        // The apartment list uses the same rule and the same default (100).
        var customers = (await sql.ListAsync(Caller, minAmount: 100m)).Value!;
        Assert.Equal(100m, customers.MinAmount);
        Assert.Equal(customers.TotalCount, (await sql.ListAsync(Caller)).Value!.TotalCount);
    }

    // ---------- instalment list: the real stored procedure against the in-memory reference model ----------
    private static async Task<(List<FakeInstalment> Rows, Dictionary<int, SnapshotCompanyRaw> Companies, List<TigerCS.Application.Modules.Collections.Dto.CollectionsTowerDto> Towers)> LoadModelAsync()
    {
        var rows = new List<FakeInstalment>(); var companies = new Dictionary<int, SnapshotCompanyRaw>(); var towers = new List<TigerCS.Application.Modules.Collections.Dto.CollectionsTowerDto>();
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using (var cmd = new Microsoft.Data.SqlClient.SqlCommand("EXEC dbo.usp_Collections_GetTowers", connection))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) towers.Add(new(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt32(3), true));
        await using (var cmd = new Microsoft.Data.SqlClient.SqlCommand("SELECT CompanyId, PaidRetained, BreakdownAvailable FROM dbo.CollectionsReceivableCompanyState WHERE CurrentRunId IS NOT NULL", connection))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) companies[r.GetInt32(0)] = Snapshot.FakeInstalmentSource.Loaded(r.GetInt32(0), DateTime.UtcNow.AddMinutes(-1), r.GetBoolean(1), r.GetBoolean(2));
        await using (var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"SELECT s.CompanyId, s.TenantId, s.FullName, s.UnitId, s.UnitCode, s.VoucherNumber, s.DueDate, s.OriginalAmount, s.PaidAmount, s.Amount, s.SourceStatus, st.BreakdownAvailable
            FROM dbo.CollectionsReceivableSnapshot s JOIN dbo.CollectionsReceivableCompanyState st ON st.CompanyId = s.CompanyId AND st.CurrentRunId = s.RunId", connection) { CommandTimeout = 120 })
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                rows.Add(new FakeInstalment(r.GetInt32(0), r.GetString(1), r.GetString(2), (int)r.GetInt64(3), r.GetString(4), r.GetString(5), r.GetDateTime(6),
                    r.IsDBNull(7) ? null : r.GetDecimal(7), r.IsDBNull(8) ? null : r.GetDecimal(8), r.GetDecimal(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetBoolean(11)));
        return (rows, companies, towers);
    }

    public static IEnumerable<object?[]> InstalmentScenarios()
    {
        // tower, from, to, paymentFilter, min, page, search
        yield return [null, "2026-01-01", "2026-10-31", "outstanding", 100m, 1, null];
        yield return [null, "2026-02-01", "2026-02-28", "outstanding", 100m, 1, null];       // a month
        yield return [null, "2026-01-01", "2026-01-31", "outstanding", 0m, 2, null];
        yield return [5, "2026-01-01", "2026-10-31", "outstanding", 100m, 1, null];
        yield return [null, "2026-01-01", "2026-12-31", "unpaid", 100m, 1, null];
        yield return [null, "2026-01-01", "2026-12-31", "partial", 100m, 3, null];
        yield return [null, "2026-01-01", "2026-10-31", "paid", 99999m, 1, null];            // the minimum must be ignored
        yield return [null, "2026-01-01", "2026-10-31", "all", 99999m, 2, null];
        yield return [5, "2026-03-01", "2026-03-31", "all", 100m, 1, null];
        yield return [null, "2025-11-01", "2026-04-30", "outstanding", 1000m, 1, null];
        yield return [null, "2026-01-01", "2026-10-31", "outstanding", 100m, 1, "Customer 1234"];
        yield return [null, "2026-01-01", "2026-10-31", "outstanding", 100m, 1, "INV-1234-"];
    }

    [Theory]
    [MemberData(nameof(InstalmentScenarios))]
    public async Task InstalmentPage_RealStoredProcedure_MatchesTheReferenceModel(int? tower, string from, string to, string filter, decimal min, int page, string? search)
    {
        if (string.IsNullOrEmpty(ConnectionString)) return;
        var (_, _, source) = Build();
        var (rows, companies, towers) = await LoadModelAsync();
        var model = new Snapshot.FakeInstalmentSource(DateTime.UtcNow);
        model.Rows.AddRange(rows); model.Towers.Clear(); model.Towers.AddRange(towers); model.Companies.Clear(); foreach (var (k, v) in companies) model.Companies[k] = v;
        var request = new PactInstalmentsRequest(DateOnly.Parse(from), DateOnly.Parse(to), new DateOnly(2026, 10, 1), min, filter, search, null, page, 25, null, tower);
        var real = await source.ReadInstalmentsAsync(request, default);
        var expected = await model.ReadInstalmentsAsync(request, default);
        Assert.Equal(expected.Unavailable, real.Unavailable);
        Assert.Equal(expected.Totals, real.Totals);
        Assert.Equal(expected.Rows.Select(Key), real.Rows.Select(Key));
        if (!real.Unavailable && filter is "outstanding" or "unpaid" or "partial") Assert.All(real.Rows, r => Assert.True(r.RemainingAmount >= min));
        if (filter is "paid" or "all") Assert.Equal(0, real.Rows.Count(r => r.RemainingAmount < 0));
    }

    [Theory]
    [MemberData(nameof(InstalmentScenarios))]
    public async Task UnitPage_RealStoredProcedure_MatchesTheReferenceModel_AndTheInstalmentView(int? tower, string from, string to, string filter, decimal min, int page, string? search)
    {
        if (string.IsNullOrEmpty(ConnectionString)) return;
        var (_, _, source) = Build();
        var (rows, companies, towers) = await LoadModelAsync();
        var model = new Snapshot.FakeInstalmentSource(DateTime.UtcNow);
        model.Rows.AddRange(rows); model.Towers.Clear(); model.Towers.AddRange(towers); model.Companies.Clear(); foreach (var (k, v) in companies) model.Companies[k] = v;
        var request = new PactInstalmentsRequest(DateOnly.Parse(from), DateOnly.Parse(to), new DateOnly(2026, 10, 1), min, filter, search, null, page, 25, null, tower);
        var real = await source.ReadInstalmentUnitsAsync(request, default);
        var expected = await model.ReadInstalmentUnitsAsync(request, default);
        Assert.Equal(expected.Unavailable, real.Unavailable);
        Assert.Equal(expected.Totals, real.Totals);                                                       // counts, amounts, unit count over ALL pages
        Assert.Equal(expected.Units.Select(UnitKey), real.Units.Select(UnitKey));                        // same units, same order, same page
        foreach (var (e, r) in expected.Units.Zip(real.Units))
        {
            Assert.Equal(e.Instalments.Select(Key), r.Instalments.Select(Key));                           // expansion: every matching instalment of the unit, nothing else
            Assert.Equal(r.InstalmentCount, r.Instalments.Count);
            Assert.Equal(r.RemainingTotal, r.Instalments.Sum(i => i.RemainingAmount));
            Assert.Equal(r.OldestDueDate, r.Instalments.Min(i => i.DueDate));
        }
        // Same filters, same instalments: the instalment view's totals are the unit view's totals.
        var flat = await source.ReadInstalmentsAsync(request, default);
        Assert.Equal(flat.Totals with { UnitCount = real.Totals.UnitCount }, real.Totals);
    }

    [Fact]
    public async Task UnitPages_WalkedToTheEnd_AreDisjointComplete_AndAddUpToTheInstalmentView()
    {
        if (string.IsNullOrEmpty(ConnectionString)) return;
        var (_, _, source) = Build();
        PactInstalmentsRequest Request(int page, int size) => new(new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 31), new DateOnly(2026, 10, 1), 100m, "outstanding", null, null, page, size, null, 5);
        var first = await source.ReadInstalmentUnitsAsync(Request(1, 100), default);
        var pages = (int)Math.Ceiling(first.Totals.UnitCount / 100.0);
        var units = new List<TigerCS.Application.Modules.Collections.Dto.PactInstalmentUnitDto>(first.Units);
        for (var p = 2; p <= pages; p++) units.AddRange((await source.ReadInstalmentUnitsAsync(Request(p, 100), default)).Units);
        Assert.Equal(first.Totals.UnitCount, units.Count);
        Assert.Equal(units.Count, units.Select(UnitKey).Distinct().Count());                              // no unit on two pages
        Assert.Equal(first.Totals.Count, units.Sum(u => u.InstalmentCount));                               // no instalment lost or duplicated
        Assert.Equal(first.Totals.RemainingTotal, units.Sum(u => u.RemainingTotal));
        Assert.True(units.Zip(units.Skip(1)).All(p => p.First.OldestDueDate <= p.Second.OldestDueDate));    // oldest first across pages
        // Different companies / customers stay separate: no (company, customer, unit code, unit id) twice.
        Assert.Equal(units.Count, units.Select(u => (u.CompanyId, u.TenantId, u.UnitId, u.UnitCode)).Distinct().Count());
        // And it equals the instalment view walked to the end.
        var flat = new List<TigerCS.Application.Modules.Collections.Dto.PactInstalmentRowDto>();
        for (var p = 1; p <= (int)Math.Ceiling(first.Totals.Count / 100.0); p++) flat.AddRange((await source.ReadInstalmentsAsync(Request(p, 100), default)).Rows);
        Assert.Equal(flat.Select(Key).Order(), units.SelectMany(u => u.Instalments).Select(Key).Order());
    }

    [Fact]
    public async Task UnitView_RealData_ExpansionsMatchUnitTotalsAndMonthlyAmounts_AndMonthCardsFilterBothViews()
    {
        if (string.IsNullOrEmpty(ConnectionString)) return;
        var options = new TigerCS.Application.Modules.Collections.CollectionsOptions { Enabled = true };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TigerCsDatabase"] = ConnectionString }).Build();
        var service = new PactInstalmentsAppService(options, new PactReceivablesOptions { Enabled = true }, new CollectionsAuthorizationService(options, new FakeDepartmentRepository()),
            new CollectionsClock(options, new FakeTimeProvider(new DateTime(2026, 10, 9, 5, 0, 0, DateTimeKind.Utc))),
            new SnapshotPactReceivablesSource(config, new ReceivablesSnapshotOptions { MaxAgeMinutes = 100000 }, new PactReceivablesOptions { Enabled = true }, TimeProvider.System, NullLogger<SnapshotPactReceivablesSource>.Instance),
            NullLogger<PactInstalmentsAppService>.Instance);
        Task<CollectionsResult<TigerCS.Application.Modules.Collections.Dto.PactInstalmentsPageDto>> List(string view, string? month, int page = 1, int size = 100) =>
            service.ListAsync(Caller, 5, new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 31), "outstanding", 100m, null, page, size, default, view, month);

        var all = (await List("units", null)).Value!;
        // The month cards were removed from the page; the overview is only read when asked for, so the card checks below run only when it is present.
        foreach (var unit in all.Units!)
        {
            Assert.Equal(unit.InstalmentCount, unit.Instalments.Count);
            Assert.Equal(unit.RemainingTotal, unit.Instalments.Sum(i => i.RemainingAmount));
            var perMonth = unit.Instalments.Where(i => i.RemainingAmount > 0).GroupBy(i => (i.DueDate.Year, i.DueDate.Month)).ToDictionary(g => g.Key, g => g.Sum(i => i.RemainingAmount));
            Assert.Equal(unit.RemainingTotal, perMonth.Values.Sum());                                  // the monthly amounts add up to the unit total
            // Due / Overdue / Not yet due is the date rule of the business month: one label per month.
            foreach (var g in unit.Instalments.GroupBy(i => (i.DueDate.Year, i.DueDate.Month)))
            {
                var expected = (g.Key.Year, g.Key.Month).CompareTo((2026, 10)) switch { < 0 => "Overdue", 0 => "Due", _ => "NotYetDue" };
                Assert.All(g.Where(i => i.RemainingAmount > 0), i => Assert.Equal(expected, i.Classification));
            }
        }
        foreach (var card in all.Months ?? [])
        {
            var key = $"{card.Year}-{card.Month:00}";
            var byUnit = (await List("units", key)).Value!;
            var byInstalment = (await List("instalments", key)).Value!;
            Assert.Equal((card.InstalmentCount, card.RemainingTotal, card.OverdueCount, card.OverdueRemaining), (byUnit.Totals.Count, byUnit.Totals.RemainingTotal, byUnit.Totals.OverdueCount, byUnit.Totals.OverdueRemaining));
            Assert.Equal(byInstalment.Totals with { UnitCount = byUnit.Totals.UnitCount }, byUnit.Totals);               // both views, same month filter, same totals
            Assert.All(byUnit.Units!.SelectMany(u => u.Instalments), i => Assert.Equal((card.Year, card.Month), (i.DueDate.Year, i.DueDate.Month)));
            Assert.True(byUnit.Units!.Sum(u => u.RemainingTotal) <= card.RemainingTotal);                                 // one page of units never exceeds the whole month
        }
    }

    private static string UnitKey(TigerCS.Application.Modules.Collections.Dto.PactInstalmentUnitDto u) =>
        $"{u.CompanyId}|{u.TenantId}|{u.UnitId}|{u.UnitCode}|{u.InstalmentCount}|{u.RemainingTotal}|{u.OldestDueDate:yyyy-MM-dd}|{u.CustomerName}";

    private static string Key(TigerCS.Application.Modules.Collections.Dto.PactInstalmentRowDto r) =>
        $"{r.CompanyId}|{r.TenantId}|{r.UnitCode}|{r.VoucherNumber}|{r.DueDate:yyyy-MM-dd}|{r.OriginalAmount}|{r.PaidAmount}|{r.RemainingAmount}|{r.PaymentStatus}|{r.Classification}";

    [Fact]
    public async Task StoredPaymentStatus_AgreesWithTheDomainClassifier_ForEveryRow()
    {
        if (string.IsNullOrEmpty(ConnectionString)) return;
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(ConnectionString);
        await connection.OpenAsync();
        var wrong = 0; var total = 0;
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"SELECT s.Amount, s.OriginalAmount, s.PaidAmount, s.SourceStatus, s.PaymentStatus, st.BreakdownAvailable
            FROM dbo.CollectionsReceivableSnapshot s JOIN dbo.CollectionsReceivableCompanyState st ON st.CompanyId = s.CompanyId AND st.CurrentRunId = s.RunId", connection) { CommandTimeout = 120 };
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            total++;
            var expected = TigerCS.Domain.Modules.Collections.CollectionsPaymentFilters.Classify(r.GetDecimal(0), r.IsDBNull(1) ? null : r.GetDecimal(1), r.IsDBNull(2) ? null : r.GetDecimal(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetBoolean(5));
            if (expected != r.GetString(4)) wrong++;
        }
        Assert.True(total > 0);
        Assert.Equal(0, wrong);
    }

    [Fact]
    public async Task Benchmark_Writes_Timings()
    {
        if (string.IsNullOrEmpty(ConnectionString)) return;
        var (sql, memory, _) = Build();
        var lines = new List<string>();
        foreach (var (name, tower, from, min) in new[] { ("all towers, default window, min 100", (int?)null, (DateOnly?)null, 100m), ("all towers, default window, min 0", null, null, 0m),
            ("tower 5, min 100", 5, null, 100m), ("all towers, 2025-04-09..2026-10-31, min 100", null, new DateOnly(2025, 4, 9), 100m) })
            foreach (var (path, service) in new[] { ("SQL-paged (new)", sql), ("rows->memory (old rules)", memory) })
            {
                var times = new List<double>(); long alloc = 0; var apartments = 0; double sqlMs = 0;
                for (var i = 0; i < 5; i++)
                {
                    var before = GC.GetTotalAllocatedBytes(true);
                    var sw = Stopwatch.StartNew();
                    var result = await service.ListAsync(Caller, towerId: tower, dateFrom: from, minAmount: min);
                    sw.Stop();
                    Assert.True(result.IsSuccess, result.Detail);
                    times.Add(sw.Elapsed.TotalMilliseconds); alloc = GC.GetTotalAllocatedBytes(true) - before; apartments = result.Value!.TotalCount; sqlMs = result.Value.Timings?.SourceMs ?? 0;
                }
                lines.Add($"{name} | {path} | apartments={apartments} | first={times[0]:F0}ms | warm median={times.Skip(1).OrderBy(x => x).ElementAt(1):F0}ms | source={sqlMs:F0}ms | allocated/request={alloc / 1_000_000.0:F0} MB");
            }
        // Instalment-level list (the new Receivables page): service call = SQL filter + totals + paging + mapping of ONE page.
        var options = new CollectionsOptions { Enabled = true };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TigerCsDatabase"] = ConnectionString }).Build();
        var instalments = new PactInstalmentsAppService(options, new PactReceivablesOptions { Enabled = true }, new CollectionsAuthorizationService(options, new FakeDepartmentRepository()),
            new CollectionsClock(options, new FakeTimeProvider(new DateTime(2026, 10, 9, 5, 0, 0, DateTimeKind.Utc))),
            new SnapshotPactReceivablesSource(config, new ReceivablesSnapshotOptions { MaxAgeMinutes = 100000 }, new PactReceivablesOptions { Enabled = true }, TimeProvider.System, NullLogger<SnapshotPactReceivablesSource>.Instance),
            NullLogger<PactInstalmentsAppService>.Instance);
        foreach (var (name, tower, from, to, status) in new[] { ("instalments: default window, outstanding", (int?)null, (DateOnly?)null, (DateOnly?)null, "outstanding"),
            ("instalments: Feb 2026 (month), outstanding", null, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), "outstanding"), ("instalments: tower 5, outstanding", 5, null, null, "outstanding"),
            ("instalments: default window, all (paid + unpaid)", null, null, null, "all"), ("instalments: default window, paid", null, null, null, "paid") })
        {
            var times = new List<double>(); long alloc = 0; var count = 0;
            for (var i = 0; i < 5; i++)
            {
                var before = GC.GetTotalAllocatedBytes(true);
                var sw = Stopwatch.StartNew();
                var result = await instalments.ListAsync(Caller, towerId: tower, dateFrom: from, dateTo: to, paymentStatus: status);
                sw.Stop();
                Assert.True(result.IsSuccess, result.Detail);
                times.Add(sw.Elapsed.TotalMilliseconds); alloc = GC.GetTotalAllocatedBytes(true) - before; count = result.Value!.Totals.Count;
            }
            lines.Add($"{name} | SQL-paged (new) | instalments={count} | first={times[0]:F0}ms | warm median={times.Skip(1).OrderBy(x => x).ElementAt(1):F0}ms | allocated/request={alloc / 1_000_000.0:F1} MB");
        }
        File.WriteAllLines("/tmp/claude-0/perf/after_csharp.txt", lines);
    }
}

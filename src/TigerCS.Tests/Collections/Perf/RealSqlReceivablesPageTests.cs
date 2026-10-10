using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Modules.Collections;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Perf;

/// <summary>
/// Receivables page and Campaigns table against a REAL SQL Server (V001-V009 deployed, synthetic PACT data from tests/local-sqlserver 01-04 and one refresh).
/// Runs only when TIGERCS_PERF_SQL holds a connection string (never in CI); otherwise every test returns at once. The tenants VER-01..VER-07 (04_receivables_page_cases.sql)
/// are dated for TODAY (Dubai) = 2026-10-10, which is the clock used here.
/// </summary>
public sealed class RealSqlReceivablesPageTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TIGERCS_PERF_SQL");
    private static readonly DateTime Clock = new(2026, 10, 10, 5, 0, 0, DateTimeKind.Utc);   // 09:00 in Dubai
    private static readonly DateOnly Today = new(2026, 10, 10), Start = new(2000, 1, 1);
    private static readonly CollectionsCaller Caller = new(Guid.NewGuid(), [Roles.CsManager], []);

    private sealed class RowsOnly(IPactReceivablesSource inner) : IPactReceivablesSource   // hides the SQL campaign engine: the application's in-memory reference path
    {
        public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) => inner.ReadAsync(throughDate, cancellationToken);
        public Task<PactReceivablesSnapshot> ReadAsync(PactReceivablesRequest request, CancellationToken cancellationToken) => inner.ReadAsync(request, cancellationToken);
    }

    private static (PactInstalmentsAppService Units, CollectionsCampaignAppService Campaign, CollectionsCampaignAppService CampaignInMemory) Build()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TigerCsDatabase"] = ConnectionString }).Build();
        var snapshotOptions = new ReceivablesSnapshotOptions { MaxAgeMinutes = 1_000_000 };
        var pact = new PactReceivablesOptions { Enabled = true, StartDate = new DateTime(2026, 1, 1) };
        var source = new SnapshotPactReceivablesSource(config, snapshotOptions, pact, TimeProvider.System, NullLogger<SnapshotPactReceivablesSource>.Instance);
        var options = new CollectionsOptions { Enabled = true };
        var auth = new CollectionsAuthorizationService(options, new FakeDepartmentRepository());
        var clock = new CollectionsClock(options, new FakeTimeProvider(Clock));
        CollectionsCampaignAppService Campaign(IPactReceivablesSource s) => new(options, new CollectionsCampaignOptions { FinancialSourceValidated = true }, pact, auth, clock, s,
            NullLogger<CollectionsCampaignAppService>.Instance);
        return (new PactInstalmentsAppService(options, pact, auth, clock, source, NullLogger<PactInstalmentsAppService>.Instance), Campaign(source), Campaign(new RowsOnly(source)));
    }

    private static Task<CollectionsResult<PactInstalmentsPageDto>> Units(PactInstalmentsAppService service, DateOnly? from = null, DateOnly? to = null, decimal? minTotal = null,
        string? status = null, int page = 1, int pageSize = 100, string search = "VER-") =>
        service.ListAsync(Caller, null, from ?? Start, to ?? Today, "outstanding", null, search, page, pageSize, default, "units", null, status, minTotal);

    private static Dictionary<string, PactInstalmentUnitDto> ByCode(PactInstalmentsPageDto page) => page.Units!.ToDictionary(u => u.UnitCode);

    [Fact]
    public async Task Receivables_UnitRows_DueOverdueTotal_FutureAndCancelledAreGone_AndDetailsAddUp()
    {
        if (ConnectionString is null) return;
        var (service, _, _) = Build();
        var page = (await Units(service)).Value!;
        var units = ByCode(page);
        Assert.Equal(["TP201-101", "TP201-102", "TP201-103", "TP201-104", "TP201-106", "TP201-107", "TP201-514"], units.Keys.Order());   // 513* and the future-only VER-05 are not there
        Assert.Equal(7, page.Totals.UnitCount);
        Assert.All(units.Values, u => Assert.Equal(u.RemainingTotal, u.Instalments.Sum(i => i.RemainingAmount)));                       // row total == its details
        Assert.Equal(page.Totals.RemainingTotal, units.Values.Sum(u => u.RemainingTotal));
        var one = units["TP201-101"];
        Assert.Equal(220m, one.RemainingTotal);                                                                                          // 60 + 60 + 100: the future 500 never counts
        Assert.Equal(["V1-A", "V1-B", "V1-C"], one.Instalments.Select(i => i.VoucherNumber));
        Assert.Equal(["Overdue", "Overdue", "Due"], one.Instalments.Select(i => i.Classification));                                      // 10 Oct 2026 is Due, earlier is Overdue
        Assert.Equal(300m, units["TP201-102"].RemainingTotal);                                                                           // the customer's other apartment is not added in
        Assert.Equal(400m, units["TP201-514"].RemainingTotal);                                                                           // the customer's other apartment of the cancelled one stays
        Assert.DoesNotContain(units.Keys, k => k.Contains('*'));
        Assert.Equal((220m + 300 + 120 + 100 + 250 + 175 + 400, 3 + 1 + 2 + 2 + 1 + 1 + 1), (page.Totals.RemainingTotal, page.Totals.Count));
        Assert.Equal((100m + 175m, 2), (page.Totals.DueRemaining, page.Totals.DueCount));                                               // Due = due TODAY only
        Assert.Equal(0, page.Totals.NotYetDueCount);                                                                                     // nothing upcoming
    }

    [Theory]
    [InlineData(null, new[] { "TP201-101", "TP201-102", "TP201-103", "TP201-104", "TP201-106", "TP201-107", "TP201-514" })]   // cleared: everything with Due or Overdue
    [InlineData(100.0, new[] { "TP201-101", "TP201-102", "TP201-103", "TP201-106", "TP201-107", "TP201-514" })]                  // 100 itself is NOT greater than 100 (VER-04); 120 from two small instalments is
    [InlineData(99.99, new[] { "TP201-101", "TP201-102", "TP201-103", "TP201-104", "TP201-106", "TP201-107", "TP201-514" })]
    [InlineData(120.0, new[] { "TP201-101", "TP201-102", "TP201-106", "TP201-107", "TP201-514" })]                               // 120 is not greater than 120
    [InlineData(300.0, new[] { "TP201-514" })]
    public async Task Receivables_MinimumTotal_IsOnTheUnitsSum_BeforeCountsAndPaging(double? min, string[] expected)
    {
        if (ConnectionString is null) return;
        var (service, _, _) = Build();
        var minTotal = min is { } m ? (decimal)m : (decimal?)null;
        var all = (await Units(service, minTotal: minTotal)).Value!;
        Assert.Equal(expected, all.Units!.Select(u => u.UnitCode).Order());
        Assert.Equal(expected.Length, all.Totals.UnitCount);
        Assert.Equal(all.Units!.Sum(u => u.RemainingTotal), all.Totals.RemainingTotal);
        var paged = new List<string>();
        for (var p = 1; p <= expected.Length; p++) paged.AddRange((await Units(service, minTotal: minTotal, page: p, pageSize: 1)).Value!.Units!.Select(u => u.UnitCode));
        Assert.Equal(expected, paged.Order());                                                                                           // pages are disjoint and complete
    }

    [Fact]
    public async Task Receivables_MonthFilter_UsesThatMonthsInstalments_ButClassifiesAgainstToday()
    {
        if (ConnectionString is null) return;
        var (service, _, _) = Build();
        var september = (await Units(service, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30))).Value!;
        var units = ByCode(september);
        Assert.Equal(["TP201-101", "TP201-102", "TP201-103", "TP201-104", "TP201-106", "TP201-514"], units.Keys.Order());
        Assert.Equal(60m, units["TP201-101"].RemainingTotal);                                                                            // only V1-B (1 Sep): the August and October ones are other months
        Assert.All(units.Values.SelectMany(u => u.Instalments), i => { Assert.Equal(9, i.DueDate.Month); Assert.Equal("Overdue", i.Classification); });   // September is Overdue by TODAY's date
        Assert.Equal(0, september.Totals.DueCount);
        var minimum = (await Units(service, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), minTotal: 100m)).Value!;                // the minimum is on the month's total
        Assert.Equal(["TP201-102", "TP201-103", "TP201-106", "TP201-514"], minimum.Units!.Select(u => u.UnitCode).Order());

        var october = (await Units(service, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31))).Value!;                              // the service stops at today
        Assert.Equal(Today, october.DateTo);
        Assert.Equal(["TP201-101", "TP201-107"], october.Units!.Select(u => u.UnitCode).Order());
        Assert.All(october.Units!.SelectMany(u => u.Instalments), i => { Assert.Equal(Today, i.DueDate); Assert.Equal("Due", i.Classification); });
        var all = (await Units(service)).Value!;
        Assert.Equal(all.Totals.RemainingTotal, ByCode(all).Values.Sum(u => u.RemainingTotal));
        var august = (await Units(service, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31))).Value!;
        Assert.Equal(60m, ByCode(august)["TP201-101"].RemainingTotal);
    }

    [Theory]
    [InlineData("overdue", new[] { "TP201-101", "TP201-102", "TP201-103", "TP201-104", "TP201-106", "TP201-514" })]
    [InlineData("due", new[] { "TP201-101", "TP201-107" })]
    public async Task Receivables_StatusFilter_KeepsUnitsWithThatStatus_AndAllTheirDueAndOverdueInstalments(string status, string[] expected)
    {
        if (ConnectionString is null) return;
        var (service, _, _) = Build();
        var page = (await Units(service, status: status)).Value!;
        Assert.Equal(expected, page.Units!.Select(u => u.UnitCode).Order());
        Assert.Equal(220m, ByCode(page)["TP201-101"].RemainingTotal);                                                                    // still its whole Due + Overdue
    }

    [Fact]
    public async Task Campaigns_SqlEngine_MatchesTheInMemoryEvaluation_ForDueOverdueTotalMinimumAndCancelledUnits()
    {
        if (ConnectionString is null) return;
        var (_, sql, memory) = Build();
        foreach (var min in new decimal?[] { null, 100m, 120m, 99.99m })
        {
            var a = (await sql.PreviewAsync(Caller, "OverdueReminder", Today, null, "VER-", 1, 100, false, default, null, null, null, min)).Value!;
            var b = (await memory.PreviewAsync(Caller, "OverdueReminder", Today, null, "VER-", 1, 100, false, default, null, null, null, min)).Value!;
            Assert.True(b.TotalCount == a.TotalCount, $"min={min}: memory [{string.Join(",", b.Items.Select(i => i.UnitCode + ":" + i.TotalAmount))}] sql [{string.Join(",", a.Items.Select(i => i.UnitCode + ":" + i.TotalAmount))}]");
            Assert.Equal(b.Items.Select(i => (i.UnitCode, i.DueAmount, i.OverdueAmount, i.TotalAmount, i.Amount)), a.Items.Select(i => (i.UnitCode, i.DueAmount, i.OverdueAmount, i.TotalAmount, i.Amount)));
            Assert.DoesNotContain(a.Items, i => i.UnitCode.Contains('*'));
            Assert.All(a.Items, i => Assert.True(i.TotalAmount > 0 && (min is null || i.TotalAmount > min)));
            Assert.Equal(new DateOnly(2026, 1, 1), a.DateFrom); Assert.Equal(Today, a.DateTo);
        }
        var none = (await sql.PreviewAsync(Caller, "OverdueReminder", Today, null, "VER-", 1, 100, false, default, null, null, null, null)).Value!;
        var byCode = none.Items.ToDictionary(i => i.UnitCode);
        // Overdue stage = instalments due before 10 Sep: TP201-101 (Aug 5 + Sep 1 = 120 stage amount), but its table amounts cover everything due up to today: Due 100, Overdue 120, Total 220.
        Assert.Equal((100m, 120m, 220m), (byCode["TP201-101"].DueAmount, byCode["TP201-101"].OverdueAmount, byCode["TP201-101"].TotalAmount));
        Assert.Equal(120m, byCode["TP201-101"].Amount);
        Assert.Equal(["TP201-101", "TP201-103", "TP201-104", "TP201-514"], none.Items.Select(i => i.UnitCode).Order());
        var withMinimum = (await sql.PreviewAsync(Caller, "OverdueReminder", Today, null, "VER-", 1, 100, false, default, null, null, null, 100m)).Value!;
        Assert.Equal(["TP201-101", "TP201-103", "TP201-514"], withMinimum.Items.Select(i => i.UnitCode).Order());                          // VER-04 totals exactly 100: hidden
        var export = (await sql.ExportAsync(Caller, "review", "review", Today, null, "VER-", default, null, null, null, 100m));
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await sql.ExportAsync(Caller, "nonsense", "review")).Outcome);
    }
}

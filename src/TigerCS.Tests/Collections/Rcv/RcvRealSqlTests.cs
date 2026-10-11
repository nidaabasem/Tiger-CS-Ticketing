using System.Data;
using Microsoft.Data.SqlClient;
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

namespace TigerCS.Tests.Collections.Rcv;

/// <summary>
/// The rebuilt receivables pipeline against a REAL SQL Server holding R001-R004 (+ V001/V003/V010) and the local fake PACT (database/collections-receivables/tests/local-sqlserver,
/// loopback linked server [10.10.10.94]; Dubai returns layout 1, Sharjah layout 3). They run only when TIGERCS_PERF_SQL holds a connection string to the throw-away TigerCsTicketing
/// database (never in CI, never a production server); otherwise they return immediately. Each test starts from an empty snapshot and its own PACT rows (fake PACT: FakeEdgeRows).
/// They prove the SQL; they cannot reproduce the production linked server (provider, MSDTC) or the real PACT volumes.
/// </summary>
[Collection("RcvRealSql")]
public sealed partial class RcvRealSqlTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("TIGERCS_PERF_SQL");
    private static readonly DateOnly Today = new(2026, 10, 11);   // the Dubai "today" every read in this class uses

    // ---- plumbing ----
    private static async Task Exec(string sql, string? database = null)
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        if (database is not null) builder.InitialCatalog = database;
        await using var connection = new SqlConnection(builder.ConnectionString); await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 }; await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> Scalar<T>(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString); await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task Reset()
    {
        await Exec("DELETE FROM dbo.CollectionsRcvLine; DELETE FROM dbo.CollectionsRcvRun; UPDATE dbo.CollectionsRcvCompany SET CurrentRunId = NULL, LastSuccessUtc = NULL, PublishedRows = 0, LastRunId = NULL, LastAttemptUtc = NULL, " +
            "LastFinishedUtc = NULL, LastStatus = 'Never', LastErrorNumber = NULL, LastError = NULL, ConsecutiveFailures = 0;");
        await Exec("UPDATE dbo.FakeConfig SET Tenants = 0, DelaySeconds = 0, Shape = CASE WHEN CompanyId = 32 THEN 3 ELSE 1 END; DELETE FROM dbo.FakeEdgeRows;", "PACTRPT");
        await Reset2();
    }

    private static async Task Pact(int company, string unitCode, string tenant, string name, string mobile, string email, int unitId, string voucher, string due, decimal amount, string status = "Installment")
    {
        static string Q(string s) => s.Replace("'", "''");
        await Exec($"INSERT dbo.FakeEdgeRows (CompanyId, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status) " +
            $"VALUES ({company}, '', N'{Q(unitCode)}', '{Q(tenant)}', N'{Q(name)}', '{Q(mobile)}', '{Q(email)}', {unitId}, '{Q(voucher)}', '', '{due}', {amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}, '{status}')", "PACTRPT");
    }

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TigerCsDatabase"] = ConnectionString }).Build();
    private static RcvReceivablesRefresher Refresher(int timeoutSeconds = 1800) =>
        new(Config(), new ReceivablesSnapshotOptions { RefreshCommandTimeoutSeconds = timeoutSeconds }, NullLogger<RcvReceivablesRefresher>.Instance);
    private static RcvReceivablesSource Source() =>
        new(Config(), new ReceivablesSnapshotOptions(), new PactReceivablesOptions { Enabled = true }, TimeProvider.System, NullLogger<RcvReceivablesSource>.Instance);

    private static async Task<PactInstalmentUnitsPage> Page(decimal? minTotal = 100m, string? status = null, DateOnly? from = null, DateOnly? to = null, string? search = null,
        int? company = null, int page = 1, int pageSize = 100) =>
        await Source().ReadInstalmentUnitsAsync(new PactInstalmentsRequest(from ?? new DateOnly(2000, 1, 1), to ?? Today, Today, 0m, "outstanding", search,
            search is not null && search.All(c => char.IsDigit(c) || c == '+') ? new string(search.Where(char.IsDigit).ToArray()) : null, page, pageSize, company, null, status, true, minTotal), default);

    private static string Key(PactInstalmentUnitDto u) => $"{u.CompanyId}:{u.TenantId}:{u.UnitCode}";

    /// <summary>The scenario every read test uses (Today = 2026-10-11). Dubai (4) in layout 1, Sharjah (32) in layout 3.</summary>
    private static async Task Seed()
    {
        await Reset();
        // D1 owns two apartments. A: 60 + 60 (two small payments = 120 > 100). B: exactly 100, due today, plus a payment due in November (future: never read).
        await Pact(4, "TP101-101", "D1", "Dina One", "+971500000001", "dina@example.test", 1011, "A1", "2026-09-15", 60);
        await Pact(4, "TP101-101", "D1", "Dina One", "+971500000001", "dina@example.test", 1011, "A2", "2026-10-01", 60);
        await Pact(4, "TP101-102", "D1", "Dina One", "+971500000001", "dina@example.test", 1012, "B1", "2026-10-11", 100);
        await Pact(4, "TP101-102", "D1", "Dina One", "+971500000001", "dina@example.test", 1012, "B2", "2026-11-15", 500);
        // D2: a payment from a previous YEAR (300), a recent partial balance (50), and a settled one (0).
        await Pact(4, "TP102-201", "D2", "Dani Two", "+971500000002", "dani@example.test", 2021, "C1", "2025-03-10", 300);
        await Pact(4, "TP102-201", "D2", "Dani Two", "+971500000002", "dani@example.test", 2021, "C2", "2026-09-20", 50);
        await Pact(4, "TP102-201", "D2", "Dani Two", "+971500000002", "dani@example.test", 2021, "C3", "2026-01-01", 0, "Paid");
        // D3: a cancelled apartment (513*) next to a live one of the same customer.
        await Pact(4, "TP103-513*", "D3", "Dara Three", "+971500000003", "dara@example.test", 3135, "E1", "2026-08-01", 900);
        await Pact(4, "TP103-514", "D3", "Dara Three", "+971500000003", "dara@example.test", 3140, "E2", "2026-08-01", 200);
        // D4: invalid units - UnitID 0, unit code 0, blank unit code.
        await Pact(4, "TP104-401", "D4", "Dino Four", "+971500000004", "dino@example.test", 0, "F1", "2026-08-01", 500);
        await Pact(4, "0", "D4", "Dino Four", "+971500000004", "dino@example.test", 4005, "F2", "2026-08-01", 500);
        await Pact(4, "", "D4", "Dino Four", "+971500000004", "dino@example.test", 4006, "F3", "2026-08-01", 500);
        // D5: no mobile and no e-mail - still owes money.
        await Pact(4, "TP105-501", "D5", "Dalia Five", "", "", 5011, "G1", "2026-09-30", 150);
        // Sharjah: the SAME unit code as Dubai's TP101-101 for another customer, and a contact-less customer.
        await Pact(32, "TP101-101", "S1", "Sami One", "+971500000011", "sami@example.test", 1011, "H1", "2026-10-02", 40);
        await Pact(32, "TP127-301", "S2", "Sara Two", "", "", 3011, "H2", "2026-10-05", 250);
        await Pact(32, "TP140-302", "S2", "Sara Two", "", "", 3012, "H3", "2026-10-11", 75);
        var result = await Refresher().RefreshAsync("Test", null, default);
        Assert.Equal("Succeeded", result.Status);
    }

    // ---- 1. fetch, validate, publish, per company ----
    [Fact]
    public async Task Refresh_LoadsEachCompanyFromItsOwnProcedure_KeepsThemApart_AndCountsWhatItLeftOut()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Seed();
        Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM dbo.CollectionsRcvCompany WHERE CurrentRunId IS NOT NULL AND LastStatus = 'Succeeded'"));
        // Dubai: 12 PACT rows with a positive amount... minus the cancelled (E1), the three invalid units (F1-F3); the settled one (C3) and the future November one stay out / in as shown.
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM dbo.CollectionsRcvLine WHERE CompanyId = 4 AND (UnitCode LIKE '%*%' OR UnitId <= 0 OR UnitCode IN ('', '0') OR Amount <= 0)"));
        var run4 = await Scalar<string>("SELECT CONCAT(RawRows, '/', PublishedRows, '/', ExcludedSettledRows, '/', ExcludedInvalidRows, '/', ExcludedCancelledRows, '/', ExcludedCancelledAmount, '/', Layout) FROM dbo.CollectionsRcvRun WHERE CompanyId = 4");
        Assert.Equal("13/8/1/3/1/900.0000/1", run4);
        var run32 = await Scalar<string>("SELECT CONCAT(RawRows, '/', PublishedRows, '/', ExcludedSettledRows, '/', ExcludedInvalidRows, '/', ExcludedCancelledRows, '/', Layout) FROM dbo.CollectionsRcvRun WHERE CompanyId = 32");
        Assert.Equal("3/3/0/0/0/3", run32);                                       // Sharjah: its own 12-column procedure, no ProjectCode
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM dbo.CollectionsRcvLine l JOIN dbo.CollectionsRcvCompany c ON c.CompanyId = l.CompanyId AND c.CurrentRunId <> l.RunId"));   // only published lines exist
        Assert.Equal(8, await Scalar<int>("SELECT COUNT(*) FROM dbo.CollectionsRcvLine WHERE CompanyId = 4"));
        Assert.Equal(3, await Scalar<int>("SELECT COUNT(*) FROM dbo.CollectionsRcvLine WHERE CompanyId = 32"));
        Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM dbo.CollectionsRcvLine WHERE CompanyId = 32 AND Mobile = '' AND Email = ''"));   // rows without a mobile or e-mail are kept
    }

    [Fact]
    public async Task ARefreshOfOneCompany_NeverTouchesTheOther()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Seed();
        var dubaiRun = await Scalar<string>("SELECT CAST(CurrentRunId AS varchar(40)) FROM dbo.CollectionsRcvCompany WHERE CompanyId = 4");
        var result = await Refresher().RefreshAsync("Retry", 32, default);
        Assert.Equal("Succeeded", result.Status);
        Assert.Single(result.Companies);
        Assert.Equal(dubaiRun, await Scalar<string>("SELECT CAST(CurrentRunId AS varchar(40)) FROM dbo.CollectionsRcvCompany WHERE CompanyId = 4"));
        Assert.Equal(3, await Scalar<int>("SELECT COUNT(*) FROM dbo.CollectionsRcvLine WHERE CompanyId = 32"));      // replaced, not duplicated
    }

    // ---- 2. what the Receivables page shows ----
    [Fact]
    public async Task TheDefaultView_ListsEachApartmentOnce_WithDueOverdueAndTotal_NewestFirst_AndHidesWhatMustNotShow()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Seed();
        var page = await Page();           // Minimum Total 100 (strictly greater)
        var keys = page.Units.Select(Key).ToList();
        // Listed: A (120 = 60 + 60), D2's apartment (350), D3's live apartment (200), D5 (150), Sharjah TP101-101 (40 < 100 hidden), TP127-301 (250). Not listed: B (exactly 100), the 75 one.
        Assert.Equal(new[] { "4:D1:TP101-101", "4:D5:TP105-501", "4:D2:TP102-201", "4:D3:TP103-514", "32:S2:TP127-301" }.Order(), keys.Order());
        Assert.DoesNotContain("4:D1:TP101-102", keys);                                     // 100 is not greater than 100
        Assert.DoesNotContain(keys, k => k.Contains('*') || k.Contains("D4"));              // cancelled and invalid units never appear
        Assert.Equal(page.Units.Select(u => u.LatestDueDate).OrderByDescending(d => d), page.Units.Select(u => u.LatestDueDate));   // apartments: newest payment first
        var a = page.Units.Single(u => u.UnitCode == "TP101-101" && u.CompanyId == 4);
        Assert.Equal(120m, a.RemainingTotal);                                              // 60 + 60: two small payments make one apartment above 100
        Assert.Equal(["A2", "A1"], a.Instalments.Select(i => i.VoucherNumber));            // details newest first
        Assert.All(page.Units, u => Assert.Equal(u.RemainingTotal, u.Instalments.Sum(i => i.RemainingAmount)));   // details add up to the row's Total
        Assert.All(page.Units, u => Assert.All(u.Instalments, i => Assert.True(i.DueDate <= Today)));              // nothing in the future
        Assert.Equal(page.Totals.UnitCount, page.Units.Count);
        Assert.Equal(page.Totals.RemainingTotal, page.Units.Sum(u => u.RemainingTotal));
        Assert.Equal(page.Totals.Count, page.Units.Sum(u => u.InstalmentCount));
        // The same customer's second apartment is its own row, never merged.
        var d1 = await Page(minTotal: null, search: "Dina");
        Assert.Equal(["TP101-101", "TP101-102"], d1.Units.Select(u => u.UnitCode).Order());
        Assert.Equal(100m, d1.Units.Single(u => u.UnitCode == "TP101-102").RemainingTotal);        // the 500 due in November is not in it
    }

    [Fact]
    public async Task MinimumTotal_IsStrictlyGreater_AppliesToTheApartmentSum_AndClearingShowsEverythingDueOrOverdue()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Seed();
        Assert.DoesNotContain("4:D1:TP101-102", (await Page(minTotal: 100m)).Units.Select(Key));        // a sum of exactly 100 is hidden
        Assert.Contains("4:D1:TP101-102", (await Page(minTotal: 99.99m)).Units.Select(Key));
        var cleared = await Page(minTotal: null);
        Assert.Contains("32:S1:TP101-101", cleared.Units.Select(Key));                                  // 40: only shown when the minimum is cleared
        Assert.Equal(8, cleared.Totals.UnitCount);                                                      // D1 A + B, D2, D3-514, D5, S1 and S2's two apartments (see the seed)
        Assert.Equal(0, (await Page(minTotal: 100000m)).Totals.UnitCount);
        // The count, the pages and the totals all follow the same filter.
        var paged = new List<string>();
        for (var p = 1; p <= 3; p++) paged.AddRange((await Page(minTotal: null, pageSize: 4, page: p)).Units.Select(Key));
        Assert.Equal(cleared.Units.Select(Key).Order(), paged.Order());                                 // pages are disjoint and complete
        Assert.Equal(cleared.Units.Select(Key), paged);                                                 // and in the same, stable order
    }

    [Fact]
    public async Task DueAndOverdue_AreComparedWithToday_NotWithTheMonth_AndTheStatusFilterAppliesToThePayments()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Seed();
        var all = await Page(minTotal: null);
        var b = all.Units.Single(u => u.UnitCode == "TP101-102");
        Assert.All(b.Instalments, i => Assert.Equal("Due", i.Classification));                          // due ON today
        var a = all.Units.Single(u => u.UnitCode == "TP101-101" && u.CompanyId == 4);
        Assert.All(a.Instalments, i => Assert.Equal("Overdue", i.Classification));                      // 15 Sep and 1 Oct are before today, though 1 Oct is in the same month
        var due = await Page(minTotal: null, status: "due");
        Assert.Equal(["32:S2:TP140-302", "4:D1:TP101-102"], due.Units.Select(Key).Order());              // only apartments with a payment due today, and ONLY that payment
        Assert.Equal(175m, due.Totals.DueRemaining); Assert.Equal(0m, due.Totals.OverdueRemaining);
        var overdue = await Page(minTotal: null, status: "overdue");
        Assert.DoesNotContain("4:D1:TP101-102", overdue.Units.Select(Key));
        Assert.Equal(0m, overdue.Totals.DueRemaining);
        Assert.Equal(all.Totals.RemainingTotal, due.Totals.RemainingTotal + overdue.Totals.RemainingTotal);   // Due + Overdue = everything
    }

    [Fact]
    public async Task TheMonthFilter_UsesTheDueDate_AllMonthsReachesPreviousYears_AndTheCurrentMonthStopsAtToday()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Seed();
        var allMonths = await Page(minTotal: null);
        var d2 = allMonths.Units.Single(u => u.UnitCode == "TP102-201");
        Assert.Equal(350m, d2.RemainingTotal);                                                          // 300 from March 2025 + 50
        Assert.Contains(d2.Instalments, i => i.DueDate.Year == 2025);
        var september = await Page(minTotal: null, from: new DateOnly(2026, 9, 1), to: new DateOnly(2026, 9, 30));
        Assert.Equal(50m, september.Units.Single(u => u.UnitCode == "TP102-201").RemainingTotal);       // that month's payments only, totals computed from them
        Assert.Equal(60m, september.Units.Single(u => u.UnitCode == "TP101-101" && u.CompanyId == 4).RemainingTotal);
        var october = await Page(minTotal: null, from: new DateOnly(2026, 10, 1), to: new DateOnly(2026, 10, 31));   // To is capped at today by the procedure
        Assert.DoesNotContain(october.Units.SelectMany(u => u.Instalments), i => i.DueDate > Today);
        Assert.Equal(60m, october.Units.Single(u => u.UnitCode == "TP101-101" && u.CompanyId == 4).RemainingTotal);
        var november = await Page(minTotal: null, from: new DateOnly(2026, 11, 1), to: new DateOnly(2026, 11, 30));
        Assert.Empty(november.Units);                                                                   // nothing is due in a future month yet
        var y2025 = await Page(minTotal: null, from: new DateOnly(2025, 1, 1), to: new DateOnly(2025, 12, 31));
        Assert.Equal(["4:D2:TP102-201"], y2025.Units.Select(Key));
    }

    [Fact]
    public async Task Search_FindsByNameMobileTowerAndApartment_AndNeverCrossesCompanies()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Seed();
        Assert.Equal(["4:D5:TP105-501"], (await Page(minTotal: null, search: "Dalia")).Units.Select(Key));
        Assert.Equal(["4:D2:TP102-201"], (await Page(minTotal: null, search: "971500000002")).Units.Select(Key));
        Assert.Equal(["4:D3:TP103-514"], (await Page(minTotal: null, search: "TP103-514")).Units.Select(Key));
        Assert.Equal(["4:D3:TP103-514"], (await Page(minTotal: null, search: "tp103-514")).Units.Select(Key));        // the apartment code is stored exactly but searched case-insensitively
        Assert.Equal(["4:D5:TP105-501"], (await Page(minTotal: null, search: "d5")).Units.Select(Key));             // by customer id, any case
        Assert.Equal(["4:D3:TP103-514"], (await Page(minTotal: null, search: "Tower 103")).Units.Select(Key));      // the tower NAME of dbo.CollectionsTowers
        Assert.Equal(["32:S2:TP127-301"], (await Page(minTotal: null, search: "Faradis")).Units.Select(Key));
        var sameCode = await Page(minTotal: null, search: "TP101-101");
        Assert.Equal(["32:S1:TP101-101", "4:D1:TP101-101"], sameCode.Units.Select(Key).Order());         // one code, two companies, two rows
        Assert.Equal(120m, sameCode.Units.Single(u => u.CompanyId == 4).RemainingTotal);                 // the Sharjah 40 is never added to Dubai's 120
        Assert.Equal(40m, sameCode.Units.Single(u => u.CompanyId == 32).RemainingTotal);
        Assert.Equal(["4:D1:TP101-101"], (await Page(minTotal: null, search: "TP101-101", company: 4)).Units.Select(Key));
    }

    // ---- 3. Campaigns, Payment Summary and the status share the same rows ----
    [Fact]
    public async Task TheCampaignRows_AreTheSamePayments_WithTheSameExclusions()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Seed();
        var snapshot = await Source().ReadAsync(new PactReceivablesRequest(new DateOnly(2000, 1, 1), Today), default);
        Assert.Equal(10, snapshot.Items.Count);                                                          // 8 Dubai (incl. the November one) - wait: the window ends today, so 7 + 3 Sharjah
        Assert.DoesNotContain(snapshot.Items, r => r.UnitCode.Contains('*') || r.UnitId is null or <= 0 || r.Amount <= 0 || r.DueDate.Date > Today.ToDateTime(TimeOnly.MinValue));
        Assert.Equal(new[] { 4, 32 }, snapshot.Snapshot!.Companies.Select(c => c.CompanyId).Order());
        Assert.True(snapshot.Snapshot.IsComplete); Assert.True(snapshot.Snapshot.RangeCovered);
        var sharjah = await Source().ReadAsync(new PactReceivablesRequest(null, Today, 32), default);
        Assert.All(sharjah.Items, r => Assert.Equal(32, r.CompanyId));
        Assert.Single(sharjah.Snapshot!.Companies);
    }

    [Fact]
    public async Task TheWindowRead_AppliesNoMonthClassification_DueIsTodayAndOverdueIsBeforeToday()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Seed();
        var any = await Source().ReadAsync(new PactReceivablesRequest(new DateOnly(2000, 1, 1), Today), default);
        // The old month classification (Class / AsOfDate: "Due" = anything in the as-of month) must not filter anything any more: the same rows come back whatever they ask for.
        foreach (var cls in new[] { PactReceivableClass.Due, PactReceivableClass.Overdue, PactReceivableClass.DueOrOverdue })
        {
            var read = await Source().ReadAsync(new PactReceivablesRequest(new DateOnly(2000, 1, 1), Today, Class: cls, AsOfDate: new DateOnly(2026, 9, 1)), default);
            Assert.Equal(any.Items.Count, read.Items.Count);
        }
        // What the callers in scope do with those rows: Campaigns classify by the Dubai DAY - 1 Oct is Overdue even though it is in the current month; Due is only what is due TODAY.
        var service = CampaignService(Source());
        var manager = new CollectionsCaller(Guid.NewGuid(), [Roles.CsManager], []);
        var report = (await service.PreviewAsync(manager, "CurrentMonthReminder", dateFrom: new DateOnly(2000, 1, 1), minTotal: null)).Value!;
        var a = report.Items.Single(i => i.CompanyId == 4 && i.UnitCode == "TP101-101");
        Assert.Equal((0m, 120m), (a.DueAmount, a.OverdueAmount));                                          // 15 Sep and 1 Oct: both before today (11 Oct)
        var b = report.Items.Single(i => i.CompanyId == 4 && i.UnitCode == "TP101-102");
        Assert.Equal((100m, 0m), (b.DueAmount, b.OverdueAmount));                                          // due exactly today
    }

    [Fact]
    public async Task ThePaymentSummaryRead_FindsAUnitByItsKey_PerCompany_AndNeverByContact()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Seed();
        var unit = await Source().ReadUnitAsync("TP101-101", null, Today, default);
        Assert.Equal([4, 32], unit.Identities.Select(i => i.CompanyId).Order());
        Assert.Equal(["A2", "A1"], unit.Instalments.Where(i => i.CompanyId == 4).Select(i => i.VoucherNumber));
        Assert.DoesNotContain(unit.Instalments, i => i.DueDate > Today);
        var dubaiOnly = await Source().ReadUnitAsync("TP101-101", 4, Today, default);
        Assert.Equal([4], dubaiOnly.Identities.Select(i => i.CompanyId));
        Assert.Empty((await Source().ReadUnitAsync("TP103-513*", null, Today, default)).Identities);       // a cancelled apartment has no summary
    }

    private static CollectionsCampaignAppService CampaignService(RcvReceivablesSource source)
    {
        var options = new CollectionsOptions { Enabled = true };
        return new(options, new CollectionsCampaignOptions { FinancialSourceValidated = true }, new PactReceivablesOptions { Enabled = true, DefaultMinOutstandingAmount = 0m },
            new(options, new FakeDepartmentRepository()), new(options, new FakeTimeProvider(new DateTime(2026, 10, 11, 5, 0, 0, DateTimeKind.Utc))), source, NullLogger<CollectionsCampaignAppService>.Instance);
    }

    [Fact]
    public async Task Campaigns_ListTheSameUnitsWithTheSameAmounts_KeepContactlessUnits_AndOrderNewestFirst()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Seed();
        var service = CampaignService(Source());
        var manager = new CollectionsCaller(Guid.NewGuid(), [Roles.CsManager], []);
        var from = new DateOnly(2000, 1, 1);
        // Current-month stage (1-31 Oct, nothing after today): D1's apartment A (60 on 1 Oct, 60 overdue from September), Sharjah's contactless TP127-301 (250, 5 Oct).
        var current = (await service.PreviewAsync(manager, "CurrentMonthReminder", dateFrom: from, minTotal: 100m)).Value!;
        Assert.Equal(["TP127-301", "TP101-101"], current.Items.Select(i => i.UnitCode));                    // newest payment first (5 Oct, then 1 Oct)
        var contactless = current.Items[0];
        Assert.Equal((32, "", ""), (contactless.CompanyId, contactless.Phone, contactless.Email));          // no mobile and no e-mail: the receivable is still listed ...
        Assert.Contains("NoValidContact", contactless.Reason);                                               // ... and the reason says why it cannot be sent
        Assert.Equal((0m, 250m, 250m), (contactless.DueAmount, contactless.OverdueAmount, contactless.TotalAmount));
        var a = current.Items[1];
        Assert.Equal((4, "Dina One", 0m, 120m, 120m), (a.CompanyId, a.CustomerName, a.DueAmount, a.OverdueAmount, a.TotalAmount));
        // The Receivables page shows the same apartment with the same figures.
        var page = (await Page(minTotal: 100m)).Units.Single(u => u.CompanyId == 4 && u.UnitCode == "TP101-101");
        Assert.Equal(a.TotalAmount, page.RemainingTotal);
        // Minimum Total: strictly greater, cleared = everything due or overdue (in the stage).
        Assert.Equal(["TP127-301", "TP101-101"], (await service.PreviewAsync(manager, "CurrentMonthReminder", dateFrom: from, minTotal: 119.99m)).Value!.Items.Select(i => i.UnitCode));
        Assert.Equal(["4:TP101-102", "32:TP140-302", "32:TP127-301", "4:TP101-101"],                          // minimum 40: Sharjah's own 40 (TP101-101) is not GREATER than 40; two units due today lead
            (await service.PreviewAsync(manager, "CurrentMonthReminder", dateFrom: from, minTotal: 40m)).Value!.Items.Select(i => $"{i.CompanyId}:{i.UnitCode}"));
        Assert.Equal(["TP127-301"], (await service.PreviewAsync(manager, "CurrentMonthReminder", dateFrom: from, minTotal: 120m)).Value!.Items.Select(i => i.UnitCode));   // 120 is not greater than 120
        var cleared = (await service.PreviewAsync(manager, "CurrentMonthReminder", dateFrom: from, minTotal: null)).Value!;
        Assert.Equal(5, cleared.TotalCount);                                                                  // A, B, TP101-101 of Sharjah, TP127-301, TP140-302
        // The overdue stage reaches the previous YEAR (the window starts in 2000) and still counts D2's 50 from September in its Total.
        var overdue = (await service.PreviewAsync(manager, "OverdueReminder", dateFrom: from, minTotal: null)).Value!;
        Assert.Equal(["TP102-201", "TP103-514"], overdue.Items.Select(i => i.UnitCode));
        Assert.Equal((350m, 350m), (overdue.Items[0].OverdueAmount, overdue.Items[0].TotalAmount));
        Assert.DoesNotContain(overdue.Items, i => i.UnitCode.Contains('*') || i.TenantId == "D4");           // cancelled / invalid units never reach a campaign
        // The review export is exactly the previewed rows.
        var export = (await service.ExportAsync(manager, "CurrentMonthReminder", "review", dateFrom: from, minTotal: 100m)).Value!;
        Assert.Equal(current.TotalCount, export.RowCount);
    }

    [Fact]
    public async Task TheApartmentListApi_StillWorksOnTheNewSource_AndOnlyListsWhatIsOwed()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Seed();
        var options = new CollectionsOptions { Enabled = true };
        var service = new PactReceivableCustomersAppService(options, new PactReceivablesOptions { Enabled = true, DefaultMinOutstandingAmount = 0m },
            new(options, new FakeDepartmentRepository()), new(options, new FakeTimeProvider(new DateTime(2026, 10, 11, 5, 0, 0, DateTimeKind.Utc))), Source(), NullLogger<PactReceivableCustomersAppService>.Instance);
        var report = (await service.ListAsync(new CollectionsCaller(Guid.NewGuid(), [Roles.CsAgent], []), dateFrom: new DateOnly(2000, 1, 1), minAmount: 0m, pageSize: 100)).Value!;
        Assert.DoesNotContain(report.Items, c => c.UnitCode.Contains('*') || c.TenantId == "D4");
        Assert.Contains(report.Items, c => c.TenantId == "D5" && c.UnitCode == "TP105-501");
    }

    [Fact]
    public async Task TheCrmContact_IsPreferred_AndAnAmbiguousCrmUnitIsFlagged_WithoutHidingTheReceivable()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Seed();
        var run = Guid.NewGuid();
        await Exec($"INSERT dbo.CollectionsCrmUnitOwner (RunId, UnitKey, CompanyId, ProjectCode, UnitNumber, LeadId, LeadStatus, CrmCustomerId, CrmUnitId, ProjectId, FullName, Mobile, Email, PhoneNorm, EmailNorm) VALUES " +
                   $"('{run}', N'TP101-101', 4, N'P', N'101', 1, 4, 501, 1, 1, N'Dina CRM', N'0500000001', N'', N'+971500000001', N'dina.crm@example.test'), " +
                   $"('{run}', N'TP102-201', 4, N'P', N'201', 2, 4, 601, 2, 1, N'Dani A', N'', N'', N'', N''), ('{run}', N'TP102-201', 4, N'P', N'201', 3, 4, 602, 3, 1, N'Dani B', N'', N'', N'', N''); " +
                   $"UPDATE dbo.CollectionsCrmOwnerState SET CurrentRunId = '{run}', LastSuccessUtc = SYSUTCDATETIME(), LastAttemptStatus = 'Succeeded' WHERE Id = 1;");
        try
        {
            var page = await Page(minTotal: null);
            var single = page.Units.Single(u => u.CompanyId == 4 && u.UnitCode == "TP101-101");
            Assert.Equal((1, "Dina CRM", "dina.crm@example.test"), (single.CrmCustomers, single.CrmName, single.CrmEmail));      // CRM first (PACT completes what CRM lacks, in the page model)
            var ambiguous = page.Units.Single(u => u.UnitCode == "TP102-201");
            Assert.Equal(2, ambiguous.CrmCustomers);                                                          // two CRM customers: flagged, nobody chosen, the amount is still listed
            Assert.Equal(350m, ambiguous.RemainingTotal);
            Assert.Null(ambiguous.CrmName);
            Assert.Null(page.Units.Single(u => u.UnitCode == "TP105-501").CrmCustomers);                      // a unit CRM does not hold keeps its PACT contact
            // The Sharjah apartment with the same unit CODE is not linked to Dubai's CRM owner.
            Assert.Null(page.Units.Single(u => u.CompanyId == 32 && u.UnitCode == "TP101-101").CrmCustomers);
        }
        finally { await Exec("UPDATE dbo.CollectionsCrmOwnerState SET CurrentRunId = NULL WHERE Id = 1; DELETE FROM dbo.CollectionsCrmUnitOwner;"); }
    }

    [Fact]
    public async Task Leasing_Company7_IsNeverLoadedNorRead_ItStaysAPaymentSummaryByPhoneFeature()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Seed();
        await using (var connection = new SqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var refresh = new SqlCommand("EXEC dbo.usp_CollectionsRcv_Refresh @CompanyId = 7", connection);
            var error = await Assert.ThrowsAsync<SqlException>(() => refresh.ExecuteNonQueryAsync());
            Assert.Equal(50001, error.Number);
        }
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Refresher().RefreshAsync("Test", 7, default));
        await Assert.ThrowsAsync<PactReceivablesSourceException>(() => Source().ReadAsync(new PactReceivablesRequest(null, Today, 7), default));
        await Assert.ThrowsAsync<PactReceivablesSourceException>(() => Source().ReadInstalmentUnitsAsync(new PactInstalmentsRequest(new DateOnly(2000, 1, 1), Today, Today, 0m, "outstanding", null, null, 1, 25, 7, null), default));
        await Assert.ThrowsAnyAsync<SqlException>(() => Exec("INSERT dbo.CollectionsRcvCompany (CompanyId) VALUES (7)"));          // the tables themselves only know companies 4 and 32
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM dbo.CollectionsRcvLine WHERE CompanyId NOT IN (4, 32)"));
    }

    // ---- 4. state, failure and isolation ----
    [Fact]
    public async Task BeforeTheFirstLoad_EveryCompanyIsNotLoaded_NeverZero()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Reset();
        var page = await Page();
        Assert.True(page.Snapshot.NothingLoaded);
        Assert.False(page.Snapshot.IsComplete); Assert.False(page.Snapshot.IsReady);
        Assert.All(page.Snapshot.Companies, c => { Assert.False(c.HasSnapshot); Assert.Equal("Missing", c.Freshness); Assert.False(c.RefreshInProgress); });
    }

    [Fact]
    public async Task AFailingCompany_KeepsItsLastGoodSnapshot_RecordsTheRealError_AndLeavesTheOtherCompanyAlone()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Seed();
        var before = await Page(minTotal: null);
        await Exec("CREATE OR ALTER PROCEDURE dbo.p32AccountReceivables @StartDate datetime, @EndDate datetime, @MinAmount int AS THROW 51000, N'PACT said no', 1;", "PACTRPT");
        var result = await Refresher().RefreshAsync("Test", null, default);
        Assert.Equal("PartialFailure", result.Status);
        var sharjah = result.Companies.Single(c => c.CompanyId == 32);
        Assert.Equal(("Failed", 51000), (sharjah.Status, sharjah.ErrorNumber));
        Assert.Contains("PACT said no", sharjah.ErrorMessage);
        Assert.Equal("Succeeded", result.Companies.Single(c => c.CompanyId == 4).Status);
        var after = await Page(minTotal: null);
        Assert.Equal(before.Units.Select(Key), after.Units.Select(Key));                                // Sharjah's previous snapshot still served, Dubai's new one published
        var status = after.Snapshot.Companies.Single(c => c.CompanyId == 32);
        Assert.True(status.HasSnapshot); Assert.True(status.LastRefreshFailed); Assert.Equal(1, status.ConsecutiveFailures); Assert.False(status.RefreshInProgress);
        Assert.False(after.Snapshot.Companies.Single(c => c.CompanyId == 4).LastRefreshFailed);
        // Retry of ONLY the failed company, once PACT answers again.
        await Reset2();
        var retry = await Refresher().RefreshAsync("Retry", 32, default);
        Assert.Equal("Succeeded", retry.Status);
        Assert.Equal(0, await Scalar<int>("SELECT ConsecutiveFailures FROM dbo.CollectionsRcvCompany WHERE CompanyId = 32"));
    }

    private static Task Reset2() => Exec("CREATE OR ALTER PROCEDURE dbo.p32AccountReceivables @StartDate datetime, @EndDate datetime, @MinAmount int AS EXEC dbo.FakeReceivables 32, @StartDate, @EndDate, @MinAmount;", "PACTRPT");

    [Fact]
    public async Task ARowOfAnotherCompany_FailsTheAttempt_NothingIsPublished()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Reset();
        await Pact(4, "TP101-101", "D1", "Dina One", "+971500000001", "dina@example.test", 1011, "A1", "2026-09-15", 60);
        await Exec("UPDATE dbo.FakeEdgeRows SET CompanyId = 4", "PACTRPT");
        // A Sharjah procedure that returns a Dubai row: the company check must refuse the whole result.
        await Exec("CREATE OR ALTER PROCEDURE dbo.p32AccountReceivables @StartDate datetime, @EndDate datetime, @MinAmount int AS SELECT 4 AS CompanyID, CAST('TP101-101' AS varchar(60)) AS UnitCode, CAST('D1' AS varchar(20)) AS TenantID, N'x' AS FullName, CAST('' AS varchar(30)) AS Mobile, CAST('' AS varchar(100)) AS Email, CAST(1011 AS int) AS UnitID, CAST('V' AS varchar(40)) AS VoucherNumber, CAST('' AS varchar(20)) AS ChequeNumber, CAST('2026-09-15' AS datetime) AS DueDate, CAST(60 AS float) AS Amount, CAST('Installment' AS varchar(20)) AS Status;", "PACTRPT");
        try
        {
            var result = await Refresher().RefreshAsync("Test", 32, default);
            Assert.Equal("Failed", result.Status);
            Assert.Contains("do not belong to the requested company", result.Companies.Single().ErrorMessage);
            Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM dbo.CollectionsRcvLine"));
            Assert.Null(await Scalar<string>("SELECT CAST(CurrentRunId AS varchar(40)) FROM dbo.CollectionsRcvCompany WHERE CompanyId = 32"));
        }
        finally { await Reset2(); }
    }

    [Fact]
    public async Task ASuddenlyMuchSmallerResult_IsRefused_AndThePreviousSnapshotStays()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Reset();
        for (var i = 1; i <= 300; i++) await Pact(4, $"TP101-{i}", "T" + i, "Customer " + i, "+9715000" + i.ToString("D5"), "", 10000 + i, "V" + i, "2026-09-01", 100 + i);
        Assert.Equal("Succeeded", (await Refresher().RefreshAsync("Test", 4, default)).Status);
        await Exec("DELETE FROM dbo.FakeEdgeRows WHERE CAST(SUBSTRING(TenantID, 2, 10) AS int) > 20", "PACTRPT");
        var result = await Refresher().RefreshAsync("Test", 4, default);
        Assert.Equal("Failed", result.Status);
        Assert.Contains("much smaller", result.Companies.Single().ErrorMessage);
        Assert.Equal(300, await Scalar<int>("SELECT COUNT(*) FROM dbo.CollectionsRcvLine WHERE CompanyId = 4"));
    }

    // ---- 5. lifecycle: concurrent refreshes, loading, timeout, killed session ----
    [Fact]
    public async Task WhileDubaiLoads_OnlyDubaiIsLoading_ASecondDubaiRefreshIsRefused_AndSharjahRefreshesIndependently()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Seed();
        await Exec("UPDATE dbo.FakeConfig SET DelaySeconds = 8 WHERE CompanyId = 4", "PACTRPT");
        try
        {
            var dubai = Task.Run(() => Refresher().RefreshAsync("Hangfire", 4, default));
            await WaitUntil("SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.CollectionsRcvRun WHERE CompanyId = 4 AND Status = 'Running') THEN 1 ELSE 0 END");
            var page = await Page(minTotal: null);                                                       // the previous Dubai copy is still readable ...
            var d = page.Snapshot.Companies.Single(c => c.CompanyId == 4); var s = page.Snapshot.Companies.Single(c => c.CompanyId == 32);
            Assert.True(d.HasSnapshot); Assert.True(d.RefreshInProgress); Assert.False(s.RefreshInProgress);     // ... Loading is Dubai's alone
            Assert.NotEmpty(page.Units);
            Assert.Equal("AlreadyRunning", (await Refresher().RefreshAsync("Retry", 4, default)).Status);       // single flight per company (in this process and in SQL)
            // Direct call: the SQL lock refuses even a caller that does not share this process's gate.
            await using (var connection = new SqlConnection(ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand("EXEC dbo.usp_CollectionsRcv_Refresh @CompanyId = 4, @LockWaitMs = 300", connection);
                await using var reader = await command.ExecuteReaderAsync(); await reader.ReadAsync();
                Assert.Equal("AlreadyRunning", reader.GetString(reader.GetOrdinal("Status")));
            }
            var sharjah = await Refresher().RefreshAsync("Retry", 32, default);                              // Sharjah is not blocked by Dubai
            Assert.Equal("Succeeded", sharjah.Status);
            Assert.Equal("Succeeded", (await dubai).Status);
            var done = (await Page(minTotal: null)).Snapshot;
            Assert.All(done.Companies, c => Assert.False(c.RefreshInProgress));                              // Loading ends with the attempt
        }
        finally { await Exec("UPDATE dbo.FakeConfig SET DelaySeconds = 0 WHERE CompanyId = 4", "PACTRPT"); }
    }

    private static async Task WaitUntil(string scalarSql)
    {
        for (var i = 0; i < 100; i++) { if (await Scalar<int>(scalarSql) == 1) return; await Task.Delay(100); }
        throw new TimeoutException("The expected state was not reached.");
    }

    [Fact]
    public async Task ACommandTimeout_EndsTheAttemptAsFailed_NothingStaysRunning_AndThePreviousSnapshotIsKept()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Seed();
        var previous = await Scalar<string>("SELECT CAST(CurrentRunId AS varchar(40)) FROM dbo.CollectionsRcvCompany WHERE CompanyId = 32");
        await Exec("UPDATE dbo.FakeConfig SET DelaySeconds = 20 WHERE CompanyId = 32", "PACTRPT");
        try
        {
            var result = await Refresher(timeoutSeconds: 5).RefreshAsync("Test", 32, default);              // the command times out after 5 s; PACT answers after 20 s
            Assert.Equal("Failed", result.Status);
            Assert.Equal(-2, result.Companies.Single().ErrorNumber);
            // The client gave up after 5 s, but the SQL session is still inside the (slow) PACT call: until that session ends the company is honestly "loading" and its Running row is NOT
            // closed by age. When the call returns (20 s) the session ends and the next status read closes the attempt with the reason the client noted.
            SnapshotCompanyStatusDto status;
            var waited = 0;
            while ((status = (await Page(minTotal: null)).Snapshot.Companies.Single(c => c.CompanyId == 32)).RefreshInProgress && waited++ < 60) await Task.Delay(1000);
            Assert.False(status.RefreshInProgress);                                                                    // not "loading" any more
            Assert.True(status.HasSnapshot); Assert.True(status.LastRefreshFailed);
            Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM dbo.CollectionsRcvRun WHERE Status = 'Running'"));    // nothing left behind
            Assert.Equal(previous, await Scalar<string>("SELECT CAST(CurrentRunId AS varchar(40)) FROM dbo.CollectionsRcvCompany WHERE CompanyId = 32"));
            Assert.Contains("command timeout", await Scalar<string>("SELECT LastError FROM dbo.CollectionsRcvCompany WHERE CompanyId = 32"), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(-2, await Scalar<int>("SELECT LastErrorNumber FROM dbo.CollectionsRcvCompany WHERE CompanyId = 32"));
        }
        finally { await Exec("UPDATE dbo.FakeConfig SET DelaySeconds = 0 WHERE CompanyId = 32", "PACTRPT"); }
    }

    [Fact]
    public async Task ACancelledRefresh_IsClosed_AndAnAbandonedRunningRowIsReapedOnlyWhenNoSessionHoldsTheLock()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Seed();
        // A 'Running' row whose session is gone (what a killed process leaves): nobody holds the company's lock, so the next status read closes it.
        await Exec("INSERT dbo.CollectionsRcvRun (RunId, CompanyId, TriggerSource, StartedUtc, Status) VALUES (NEWID(), 32, N'Ghost', DATEADD(SECOND, -30, SYSUTCDATETIME()), 'Running'); " +
                   "UPDATE dbo.CollectionsRcvCompany SET LastStatus = 'Running', LastAttemptUtc = DATEADD(SECOND, -30, SYSUTCDATETIME()) WHERE CompanyId = 32;");
        var status = (await Page(minTotal: null)).Snapshot.Companies.Single(c => c.CompanyId == 32);
        Assert.False(status.RefreshInProgress);                                                              // an old Running row is not "loading" by itself
        Assert.Equal("Abandoned", status.LastAttemptStatus);
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM dbo.CollectionsRcvRun WHERE Status = 'Running'"));

        // A Running row that a live session really holds (the lock is held): it stays Running, however old the row is.
        await Exec("UPDATE dbo.FakeConfig SET DelaySeconds = 6 WHERE CompanyId = 32", "PACTRPT");
        try
        {
            using var cancel = new CancellationTokenSource();
            var running = Task.Run(() => Refresher().RefreshAsync("Test", 32, cancel.Token));
            await WaitUntil("SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.CollectionsRcvRun WHERE CompanyId = 32 AND Status = 'Running') THEN 1 ELSE 0 END");
            await Exec("UPDATE dbo.CollectionsRcvRun SET StartedUtc = DATEADD(HOUR, -5, StartedUtc) WHERE CompanyId = 32 AND Status = 'Running'");   // age alone proves nothing
            Assert.True((await Page(minTotal: null)).Snapshot.Companies.Single(c => c.CompanyId == 32).RefreshInProgress);
            Assert.Equal("Succeeded", (await running).Status);
        }
        finally { await Exec("UPDATE dbo.FakeConfig SET DelaySeconds = 0 WHERE CompanyId = 32", "PACTRPT"); }
    }

    [Fact]
    public async Task ASessionThatDiesInsideTheFetch_IsReported_AndNeverLeavesCompanyLoading()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await Seed();
        // A layout that does not match the procedure kills the SQL session inside INSERT ... EXEC without running any CATCH block (measured on SQL Server 2022, as in the field).
        await using (var connection = new SqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand("EXEC dbo.usp_CollectionsRcv_Refresh @CompanyId = 32, @TriggerSource = N'WrongLayout', @Layout = 1", connection);
            await Assert.ThrowsAnyAsync<SqlException>(() => command.ExecuteReaderAsync());
        }
        var status = (await Page(minTotal: null)).Snapshot.Companies.Single(c => c.CompanyId == 32);
        Assert.False(status.RefreshInProgress);
        Assert.Equal("Abandoned", status.LastAttemptStatus);
        Assert.True(status.HasSnapshot);                                                                     // the earlier good copy is still there
        Assert.Equal(3, await Scalar<int>("SELECT COUNT(*) FROM dbo.CollectionsRcvLine WHERE CompanyId = 32"));
        Assert.Equal("Succeeded", (await Refresher().RefreshAsync("Retry", 32, default)).Status);          // and Retry works straight away
    }
}

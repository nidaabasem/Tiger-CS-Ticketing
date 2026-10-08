using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Snapshot;

/// <summary>Tower mapping, date window, Due/Overdue rules, totals and pagination for the Receivables page service.</summary>
public sealed class ReceivablesSnapshotServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 21, 0, 0, DateTimeKind.Utc); // 7 Oct 2026, Dubai

    private sealed class Harness
    {
        public FakeSnapshotSource Source { get; } = new(Now);
        public PactReceivableCustomersAppService Service { get; }
        public CollectionsCaller Agent { get; } = new(Guid.NewGuid(), [Roles.CsAgent], []);

        public Harness()
        {
            var options = new CollectionsOptions { Enabled = true };
            Service = new(options, new PactReceivablesOptions { Enabled = true }, new CollectionsAuthorizationService(options, new FakeDepartmentRepository()),
                new CollectionsClock(options, new FakeTimeProvider(Now)), Source, NullLogger<PactReceivableCustomersAppService>.Instance);
            Source.Towers.AddRange([
                new(1, "124", "Tower 124", 4, true), new(2, "136", "Tower 136", 4, true),
                new(3, "127", "Faradis", 32, true), new(4, "140", "Al Ghaf", 32, true),
                new(5, "124", "Sharjah 124", 32, true)]);
        }
    }

    private static PactReceivableInstalment Row(string unit, DateTime due, decimal amount = 100m, int company = 4, string tenant = "3001", int unitId = 101, string voucher = "V") =>
        new(company, tenant, "Example Customer", "+971500003001", "x@example.test", unitId, unit, "", voucher, "", due, amount, "Installment");

    private static DateTime D(int y, int m, int d, int h = 0, int min = 0, int s = 0, int ms = 0) => new(y, m, d, h, min, s, ms);

    [Theory]
    [InlineData("TP124-1001", 1)]
    [InlineData("124-1001", 1)]
    [InlineData("TP136-C-402", 2)]
    [InlineData("136-C-402", 2)]
    public async Task TowerFilterMatchesTheTowerNumberFromTheUnitCode_WithAnEmptyProjectCode(string unit, int towerId)
    {
        var h = new Harness();
        h.Source.Rows.AddRange([Row("TP124-1001", D(2026, 10, 5), tenant: "A", unitId: 1), Row("124-1001", D(2026, 10, 5), tenant: "B", unitId: 2),
            Row("TP136-C-402", D(2026, 10, 5), tenant: "C", unitId: 3), Row("136-C-402", D(2026, 10, 5), tenant: "D", unitId: 4)]);
        var list = (await h.Service.ListAsync(h.Agent, towerId: towerId)).Value!;
        Assert.All(list.Items, c => Assert.Equal(towerId == 1 ? "124" : "136", c.TowerNumber));
        Assert.Equal(2, list.TotalCount);
        Assert.Contains(list.Items, c => c.UnitCode == unit);
        Assert.Equal(towerId, h.Source.LastRequest!.TowerId);
    }

    [Fact]
    public async Task TowerMatchRequiresCompanyAndNumber_SameNumberOnAnotherCompanyIsADifferentTower()
    {
        var h = new Harness();
        h.Source.Rows.AddRange([Row("TP124-1", D(2026, 10, 5), tenant: "dubai", company: 4), Row("TP124-5", D(2026, 10, 5), tenant: "sharjah", company: 32, unitId: 9)]);
        Assert.Equal("dubai", Assert.Single((await h.Service.ListAsync(h.Agent, towerId: 1)).Value!.Items).TenantId);
        var sharjah = (await h.Service.ListAsync(h.Agent, towerId: 5)).Value!;
        Assert.Equal(("sharjah", 32), (Assert.Single(sharjah.Items).TenantId, sharjah.Items[0].CompanyId));
        Assert.Equal([32], sharjah.CompanyIds);   // company resolved from the tower: only company 32 is in scope
    }

    [Fact]
    public async Task AllTowersIncludesBothCompanies_AndAnUnmatchedTowerIsReportedNotDropped()
    {
        var h = new Harness();
        h.Source.Rows.AddRange([Row("TP124-1", D(2026, 10, 5), tenant: "a"), Row("TP127-7", D(2026, 10, 5), tenant: "b", company: 32, unitId: 7),
            Row("TP119-3005", D(2026, 10, 5), 250m, tenant: "c", unitId: 8)]);
        var list = (await h.Service.ListAsync(h.Agent)).Value!;
        Assert.Equal(3, list.TotalCount);
        var unmatched = list.Items.Single(c => c.TenantId == "c");
        Assert.Equal(("119", null), (unmatched.TowerNumber, unmatched.TowerName));   // no tower name is invented
        var report = Assert.Single(list.Snapshot!.UnmatchedTowers);
        Assert.Equal((4, "119", "NoMatchingTower", 250m), (report.CompanyId, report.TowerNumber, report.Reason, report.Amount));
        // Selecting a real tower must not include the unmatched receivable.
        Assert.DoesNotContain((await h.Service.ListAsync(h.Agent, towerId: 1)).Value!.Items, c => c.TenantId == "c");
    }

    [Fact]
    public async Task DefaultWindowIsJanuaryFirstToTheEndOfTheReportingMonth()
    {
        var h = new Harness();
        h.Source.Rows.Add(Row("TP124-1", D(2026, 10, 5)));
        var list = (await h.Service.ListAsync(h.Agent)).Value!;
        Assert.Equal((new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 31)), (list.DateFrom, list.DateTo));
        Assert.Equal(new PactReceivablesRequest(new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 31), null, null, PactReceivableClass.DueOrOverdue, new DateOnly(2026, 10, 1)), h.Source.LastRequest);
    }

    [Fact]
    public async Task WindowBoundaries_FromAndToAreInclusive_AndToCoversTheWholeDay()
    {
        var h = new Harness();
        h.Source.Rows.AddRange([
            Row("TP124-1", D(2025, 12, 31, 23, 59, 59, 997), tenant: "before-from", unitId: 1),
            Row("TP124-2", D(2026, 1, 1), tenant: "on-from", unitId: 2),
            Row("TP124-3", D(2026, 9, 30, 23, 59, 59, 997), tenant: "last-ms-of-to", unitId: 3),
            Row("TP124-4", D(2026, 10, 1), tenant: "after-to", unitId: 4)]);
        var list = (await h.Service.ListAsync(h.Agent, dateFrom: new DateOnly(2026, 1, 1), dateTo: new DateOnly(2026, 9, 30))).Value!;
        Assert.Equal(["last-ms-of-to", "on-from"], list.Items.Select(c => c.TenantId).Order());
        Assert.Equal(2, list.TotalCount);
    }

    [Fact]
    public async Task DefaultFromDropsPriorYearInstalments_AndEditingFromBringsThemBack()
    {
        var h = new Harness();
        h.Source.Rows.Add(Row("TP124-1", D(2025, 11, 20), tenant: "old"));
        Assert.Equal(0, (await h.Service.ListAsync(h.Agent)).Value!.TotalCount);
        var wide = (await h.Service.ListAsync(h.Agent, dateFrom: new DateOnly(2025, 1, 1))).Value!;
        Assert.Equal("old", Assert.Single(wide.Items).TenantId);
        Assert.Equal("OverDue", wide.Items[0].Instalments[0].ReceivablesType);
    }

    [Fact]
    public async Task DueAndOverdueRules_FutureInstalmentsAreNeverOverdue_AndAfterMonthEndIsNeither()
    {
        var h = new Harness();
        h.Source.Rows.AddRange([
            Row("TP124-1", D(2026, 9, 30), 10m, unitId: 1, tenant: "t1"),   // before the month -> OverDue
            Row("TP124-1", D(2026, 10, 1), 20m, unitId: 1, tenant: "t1"),   // first day of month -> Due
            Row("TP124-1", D(2026, 10, 20), 30m, unitId: 1, tenant: "t1"),  // later in the month (future vs 7 Oct) -> Due, NOT overdue
            Row("TP124-1", D(2026, 11, 1), 40m, unitId: 1, tenant: "t1")]); // after month end -> neither
        var customer = Assert.Single((await h.Service.ListAsync(h.Agent, dateTo: new DateOnly(2026, 12, 31))).Value!.Items);
        Assert.Equal(10m, customer.OverdueAmount);
        Assert.Equal(50m, customer.DueAmount);
        Assert.Equal(60m, customer.TotalAmount);
        Assert.Equal(["OverDue", "Due", "Due"], customer.Instalments.Select(i => i.ReceivablesType));
        Assert.Equal("Upcoming", customer.Instalments[2].DueTiming);
    }

    [Fact]
    public async Task WindowDoesNotChangeClassification_AWindowOfOnlyPastMonthsIsStillClassifiedFromTheReportingMonth()
    {
        var h = new Harness();
        h.Source.Rows.Add(Row("TP124-1", D(2026, 5, 15), 75m));
        var customer = Assert.Single((await h.Service.ListAsync(h.Agent, dateFrom: new DateOnly(2026, 5, 1), dateTo: new DateOnly(2026, 5, 31))).Value!.Items);
        Assert.Equal((75m, 0m, true, false), (customer.OverdueAmount, customer.DueAmount, customer.HasOverdue, customer.HasDue));
    }

    [Fact]
    public async Task PaidZeroBalanceAndInvalidUnitRowsAreNeverListed()
    {
        var h = new Harness();
        h.Source.Rows.AddRange([Row("TP124-1", D(2026, 10, 5), 0m, tenant: "paid"), Row("TP124-1", D(2026, 10, 5), 55m, tenant: "unit0", unitId: 0),
            Row("TP124-1", D(2026, 10, 5), 0.25m, tenant: "small", unitId: 3)]);
        var list = (await h.Service.ListAsync(h.Agent)).Value!;
        Assert.Equal("small", Assert.Single(list.Items).TenantId);                      // a sub-dirham positive balance stays
        Assert.Equal(0.25m, list.Items[0].DueAmount);
    }

    [Fact]
    public async Task TotalsPaginationAndStatusFilterAgreeWithEachOther()
    {
        var h = new Harness();
        for (var n = 1; n <= 60; n++)
            h.Source.Rows.Add(Row(n % 2 == 0 ? "TP124-" + n : "TP136-" + n, n % 3 == 0 ? D(2026, 9, 10) : D(2026, 10, 20), tenant: "c" + n, unitId: n));
        var all = (await h.Service.ListAsync(h.Agent, pageSize: 25)).Value!;
        Assert.Equal(60, all.TotalCount);
        Assert.Equal(60, all.DueCustomerCount + all.OverdueCustomerCount);
        var seen = new List<string>();
        for (var page = 1; page <= 3; page++) seen.AddRange((await h.Service.ListAsync(h.Agent, page: page, pageSize: 25)).Value!.Items.Select(c => c.TenantId));
        Assert.Equal(60, seen.Distinct().Count());
        var tower = (await h.Service.ListAsync(h.Agent, towerId: 1, status: "overdue")).Value!;
        Assert.Equal(tower.TotalCount, tower.OverdueCustomerCount);
        Assert.All(tower.Items, c => { Assert.Equal("124", c.TowerNumber); Assert.True(c.HasOverdue); });
        Assert.Equal(Enumerable.Range(1, 60).Count(n => n % 2 == 0 && n % 3 == 0), tower.TotalCount);
    }

    [Fact]
    public async Task UnknownTowerAndInvalidWindowAreRejected()
    {
        var h = new Harness();
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ListAsync(h.Agent, towerId: 999)).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ListAsync(h.Agent, towerId: 0)).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ListAsync(h.Agent, dateFrom: new DateOnly(2026, 5, 2), dateTo: new DateOnly(2026, 5, 1))).Outcome);
    }

    [Fact]
    public async Task TowerSelectedWithConflictingCompanyIsRejected()
    {
        var h = new Harness();
        h.Source.Rows.Add(Row("TP124-1", D(2026, 10, 5)));
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ListAsync(h.Agent, companyId: 32, towerId: 1)).Outcome);
    }

    [Fact]
    public async Task NothingEverLoaded_IsAnUnavailableError_NotAFreshEmptyList()
    {
        var h = new Harness();
        h.Source.Companies[4] = FakeSnapshotSource.NeverLoaded(4);
        h.Source.Companies[32] = FakeSnapshotSource.NeverLoaded(32);
        var result = await h.Service.ListAsync(h.Agent);
        Assert.False(result.IsSuccess);
        Assert.Equal(CollectionsOutcome.FinanceUnavailable, result.Outcome);
        Assert.Contains("not an empty result", result.Detail);
    }

    [Fact]
    public async Task PartialRefreshFailure_ServesThePreviousSnapshotOfThatCompany_AndFlagsIt()
    {
        var h = new Harness();
        h.Source.Companies[32] = FakeSnapshotSource.Failed(32, Now.AddHours(-6), Now.AddMinutes(-3));
        h.Source.Rows.AddRange([Row("TP124-1", D(2026, 10, 5), tenant: "dubai"), Row("TP127-1", D(2026, 10, 5), tenant: "sharjah-old", company: 32, unitId: 6)]);
        var list = (await h.Service.ListAsync(h.Agent)).Value!;
        Assert.Equal(2, list.TotalCount);                                  // stale company-32 rows are kept...
        var status = list.Snapshot!;
        Assert.True(status.HasFailures);                                   // ...and flagged, per company
        Assert.False(status.IsFresh);
        Assert.Equal("Fresh", status.Companies.Single(c => c.CompanyId == 4).Freshness);
        Assert.Equal("Stale", status.Companies.Single(c => c.CompanyId == 32).Freshness);
        Assert.Equal(Now.AddHours(-6), list.ReadAtUtc);
        // A tower of the healthy company is unaffected.
        Assert.True((await h.Service.ListAsync(h.Agent, towerId: 1)).Value!.Snapshot!.IsFresh);
    }

    [Fact]
    public async Task OneCompanyNeverLoaded_IsListedAsMissing_NotSilentlyTreatedAsEmpty()
    {
        var h = new Harness();
        h.Source.Companies[32] = FakeSnapshotSource.NeverLoaded(32);
        h.Source.Rows.Add(Row("TP124-1", D(2026, 10, 5)));
        var list = (await h.Service.ListAsync(h.Agent)).Value!;
        Assert.Equal(1, list.TotalCount);
        Assert.Equal("Missing", list.Snapshot!.Companies.Single(c => c.CompanyId == 32).Freshness);
        Assert.False(list.Snapshot.IsComplete);
    }

    [Fact]
    public async Task ReportMonthOverrideIsStillSupported_AndWindowDefaultsFollowIt()
    {
        var h = new Harness();
        h.Source.Rows.Add(Row("TP124-1", D(2026, 8, 20), 5m));
        var list = (await h.Service.ListAsync(h.Agent, year: 2026, month: 8)).Value!;
        Assert.Equal((new DateOnly(2026, 1, 1), new DateOnly(2026, 8, 31)), (list.DateFrom, list.DateTo));
        Assert.Equal(5m, list.Items[0].DueAmount);
    }

    [Fact]
    public void WindowNotesExplainHowMonthAndWindowInteract()
    {
        var notes = PactReceivableCustomersAppService.WindowNotes(new(2026, 3, 1), new(2026, 12, 31), new(2026, 10, 1), new(2026, 10, 31));
        Assert.Contains(notes, n => n.Contains("Overdue = due before 01 Oct 2026"));
        Assert.Contains(notes, n => n.Contains("after 31 Oct 2026 are neither Due nor Overdue"));
        Assert.DoesNotContain(notes, n => n.Contains("outside the date range"));
    }

    [Fact]
    public async Task TowerListRequiresFinancialPermission_AndComesFromTheLocalCatalog()
    {
        var options = new CollectionsOptions { Enabled = true };
        var catalog = new Catalog();
        var service = new PactReceivableCustomersAppService(options, new PactReceivablesOptions { Enabled = true },
            new CollectionsAuthorizationService(options, new FakeDepartmentRepository()), new CollectionsClock(options, new FakeTimeProvider(Now)),
            new FakeSnapshotSource(Now), NullLogger<PactReceivableCustomersAppService>.Instance, catalog);
        Assert.Equal(CollectionsOutcome.Forbidden, (await service.ListTowersAsync(new CollectionsCaller(Guid.NewGuid(), [Roles.ReportingUser], []))).Outcome);
        Assert.Equal(0, catalog.Reads);
        var towers = (await service.ListTowersAsync(new CollectionsCaller(Guid.NewGuid(), [Roles.CsAgent], []))).Value!;
        Assert.Equal("127 - Faradis", Assert.Single(towers).Label);
    }

    private sealed class Catalog : ICollectionsTowerCatalog
    {
        public int Reads { get; private set; }
        public Task<IReadOnlyList<CollectionsTowerDto>> ListActiveAsync(CancellationToken cancellationToken)
        { Reads++; return Task.FromResult<IReadOnlyList<CollectionsTowerDto>>([new CollectionsTowerDto(3, "127", "Faradis", 32, true)]); }
    }
}

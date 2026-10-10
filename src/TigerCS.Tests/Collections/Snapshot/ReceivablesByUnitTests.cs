using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Snapshot;

/// <summary>Receivables "By unit" view: same filters as the instalment view, grouped before paging, units never merged across companies or customers.</summary>
public sealed class ReceivablesByUnitTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
    private static readonly CollectionsCaller Agent = new(Guid.NewGuid(), [Roles.CsAgent], []);

    private static (PactInstalmentsAppService Service, FakeInstalmentSource Source) Build()
    {
        var source = new FakeInstalmentSource(Now);
        var options = new CollectionsOptions { Enabled = true };
        return (new PactInstalmentsAppService(options, new PactReceivablesOptions { Enabled = true }, new(options, new FakeDepartmentRepository()), new(options, new FakeTimeProvider(Now)),
            source, NullLogger<PactInstalmentsAppService>.Instance), source);
    }

    private static FakeInstalment Row(int company, string tenant, int unit, string code, int month, decimal remaining, string voucher = "V") =>
        new(company, tenant, "Customer " + tenant, unit, code, voucher + month, new DateTime(2026, month, 5), remaining + 100, 100, remaining, "Installment", true);

    [Fact]
    public async Task UnitsAreGroupedBeforePaging_AndKeepAllTheirInstalmentsTogether()
    {
        var (service, source) = Build();
        for (var u = 1; u <= 7; u++) for (var m = 1; m <= 4; m++) source.Rows.Add(Row(4, "T" + u, u, "TP124-" + u, m, 200 + u));
        var page1 = (await Agent.List(service, view: "units", pageSize: 3)).Value!;
        Assert.Equal("units", page1.View);
        Assert.Equal((7, 28), (page1.Totals.UnitCount, page1.Totals.Count));
        Assert.All(page1.Units!, u => { Assert.Equal(4, u.InstalmentCount); Assert.Equal(4, u.Instalments.Count); });   // a unit never straddles a page
        var all = new List<TigerCS.Application.Modules.Collections.Dto.PactInstalmentUnitDto>();
        for (var page = 1; page <= 3; page++) all.AddRange((await Agent.List(service, view: "units", pageSize: 3, page: page)).Value!.Units!);
        Assert.Equal(7, all.Count); Assert.Equal(7, all.Select(u => u.UnitId).Distinct().Count());      // pages are disjoint and complete
        Assert.Equal(all.Sum(u => u.RemainingTotal), page1.Totals.RemainingTotal);          // unit totals add up to the instalment totals
        Assert.Equal(all.Sum(u => u.InstalmentCount), page1.Totals.Count);
    }

    [Fact]
    public async Task DifferentCompaniesAndCustomers_AreNeverMerged_EvenWithTheSameUnitCode()
    {
        var (service, source) = Build();
        source.Rows.AddRange([Row(4, "A", 1, "TP124-1", 1, 300), Row(4, "A", 1, "TP124-1", 2, 300),
            Row(4, "B", 1, "TP124-1", 1, 300),                      // another customer in the same unit
            Row(32, "A", 1, "TP124-1", 1, 300),                     // another company, same ids
            Row(4, "a", 1, "TP124-1", 1, 300)]);                    // customer ids differing only by case are different customers
        var units = (await Agent.List(service, view: "units")).Value!.Units!;
        Assert.Equal(5 - 1, units.Count);
        Assert.Equal(2, units.Single(u => u is { CompanyId: 4, TenantId: "A" }).InstalmentCount);
        Assert.Contains(units, u => u is { CompanyId: 32, TenantId: "A", InstalmentCount: 1 });
    }

    [Fact]
    public async Task SameFiltersGiveTheSameInstalments_InBothViews()
    {
        var (service, source) = Build();
        for (var u = 1; u <= 5; u++) for (var m = 1; m <= 3; m++) source.Rows.Add(Row(4, "T" + u, u, "TP124-" + u, m, m == 1 ? 50 : 150 + u));   // the 50 balances are below the minimum
        foreach (var min in new decimal?[] { 0m, 100m, 160m })
        {
            var byInstalment = (await Agent.List(service, view: "instalments", minAmount: min, pageSize: 100)).Value!;
            var byUnit = (await Agent.List(service, view: "units", minAmount: min, pageSize: 100)).Value!;
            Assert.Equal(byInstalment.Totals with { UnitCount = byUnit.Totals.UnitCount }, byUnit.Totals);
            Assert.Equal(byInstalment.Items.Select(i => (i.UnitCode, i.VoucherNumber, i.RemainingAmount)).Order(),
                         byUnit.Units!.SelectMany(u => u.Instalments).Select(i => (i.UnitCode, i.VoucherNumber, i.RemainingAmount)).Order());
        }
    }

    private static FakeInstalment OnDay(string tenant, int unit, string code, DateTime due, decimal remaining, string voucher) =>
        new(4, tenant, "Customer " + tenant, unit, code, voucher, due, remaining + 100, 100, remaining, "Installment", true, "+971500000" + unit, tenant + "@example.test");

    [Fact]
    public async Task TheUnitView_ClassifiesByTheDay_NotTheMonth()
    {
        var (service, source) = Build();   // today (Dubai) = 14 Oct 2026
        source.Rows.AddRange([OnDay("A", 1, "TP124-1", new DateTime(2026, 10, 5), 100, "V1"),    // earlier this month: Overdue (the month rule called it Due)
            OnDay("A", 1, "TP124-1", new DateTime(2026, 10, 13), 110, "V2"),                     // yesterday: Overdue
            OnDay("A", 1, "TP124-1", new DateTime(2026, 10, 14), 120, "V3"),                     // today: Due
            OnDay("A", 1, "TP124-1", new DateTime(2026, 10, 15), 130, "V4"),                     // tomorrow: Upcoming
            OnDay("A", 1, "TP124-1", new DateTime(2026, 12, 1), 140, "V5")]);
        var page = (await service.ListAsync(Agent, null, new DateOnly(2000, 1, 1), new DateOnly(2099, 12, 31), "outstanding", 0m, null, 1, 25, default, "units")).Value!;
        // Only what is due today or earlier is listed: tomorrow's and December's instalments are not part of the unit view at all.
        Assert.Equal((2, 1, 0), (page.Totals.OverdueCount, page.Totals.DueCount, page.Totals.NotYetDueCount));
        Assert.Equal((210m, 120m, 0m), (page.Totals.OverdueRemaining, page.Totals.DueRemaining, page.Totals.NotYetDueRemaining));
        Assert.Equal(new DateOnly(2026, 10, 14), page.DateTo);
        var unit = Assert.Single(page.Units!);
        Assert.Equal(["Overdue", "Overdue", "Due"], unit.Instalments.Select(i => i.Classification));
        Assert.Equal(unit.RemainingTotal, unit.Instalments.Sum(i => i.RemainingAmount));          // the row and its instalments are the same rows
        Assert.True(source.LastRequest!.ClassifyByDay);
        Assert.Equal(new DateOnly(2026, 10, 14), source.LastRequest.AsOf);
        Assert.Null(page.Months);                                                                 // no month overview in this view
        // The instalment view keeps the month rule.
        var flat = (await service.ListAsync(Agent, null, new DateOnly(2000, 1, 1), new DateOnly(2099, 12, 31), "outstanding", 0m, null, 1, 25, default, "instalments")).Value!;
        Assert.False(source.LastRequest.ClassifyByDay);
        Assert.Equal(5, flat.Totals.Count);   // the instalment view still lists everything in its window
    }

    [Theory]
    [InlineData("overdue", "TP124-1")]
    [InlineData("due", "TP124-2")]
    public async Task TheStatusFilter_KeepsTheUnitsWithAnInstalmentOfThatStatus_AndAllTheirInstalments(string status, string expectedUnit)
    {
        var (service, source) = Build();
        source.Rows.AddRange([OnDay("A", 1, "TP124-1", new DateTime(2026, 9, 1), 100, "V1"), OnDay("A", 1, "TP124-1", new DateTime(2026, 12, 1), 50, "V1b"),
            OnDay("B", 2, "TP124-2", new DateTime(2026, 10, 14), 200, "V2"),
            OnDay("C", 3, "TP124-3", new DateTime(2026, 11, 1), 300, "V3")]);
        var page = (await service.ListAsync(Agent, null, new DateOnly(2000, 1, 1), new DateOnly(2099, 12, 31), "outstanding", 0m, null, 1, 25, default, "units", null, status)).Value!;
        var units = page.Units!;
        Assert.Equal(expectedUnit, Assert.Single(units).UnitCode);       // TP124-3 (due in November) is never listed
        Assert.Equal(units.Count, page.Totals.UnitCount);
        if (status == "overdue") Assert.Single(Assert.Single(units).Instalments);                                  // December is not due: only the 1 Sep instalment is listed
        Assert.Equal(units.Sum(u => u.RemainingTotal), page.Totals.RemainingTotal);                               // counts and totals agree with the filtered units
    }

    [Fact]
    public async Task UnitsWithoutARealNumber_AreNeverListed_AndAMalformedStatusIsRefused()
    {
        var (service, source) = Build();
        source.Rows.AddRange([OnDay("A", 0, "TP124-1", new DateTime(2026, 9, 1), 100, "V1"),       // UnitID 0
            OnDay("B", 2, "0", new DateTime(2026, 9, 1), 100, "V2"),                              // unit code "0"
            OnDay("C", 3, " ", new DateTime(2026, 9, 1), 100, "V3"),                              // blank unit code
            OnDay("D", 4, "TP124-4", new DateTime(2026, 9, 1), 100, "V4")]);
        var page = (await service.ListAsync(Agent, null, new DateOnly(2000, 1, 1), new DateOnly(2099, 12, 31), "outstanding", 0m, null, 1, 25, default, "units")).Value!;
        Assert.Equal("TP124-4", Assert.Single(page.Units!).UnitCode);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await service.ListAsync(Agent, null, null, null, null, null, null, 1, 25, default, "units", null, "soon")).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await service.ListAsync(Agent, null, null, null, null, null, null, 1, 25, default, "units", null, "upcoming")).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await service.ListAsync(Agent, null, null, null, null, null, null, 1, 25, default, "instalments", null, "overdue")).Outcome);
        Assert.True((await service.ListAsync(Agent, null, null, null, null, null, null, 1, 25, default, "units", null, "ALL")).IsSuccess);
    }

    [Fact]
    public async Task TheViewIsValidated_AndTheApiDefaultStaysPerInstalment()
    {
        var (service, source) = Build();
        source.Rows.Add(Row(4, "A", 1, "TP124-1", 1, 300));
        var defaultView = (await Agent.List(service)).Value!;
        Assert.Equal("instalments", defaultView.View); Assert.Null(defaultView.Units); Assert.Single(defaultView.Items);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await Agent.List(service, view: "nonsense")).Outcome);
    }
}

internal static class ReceivablesByUnitTestExtensions
{
    public static Task<CollectionsResult<TigerCS.Application.Modules.Collections.Dto.PactInstalmentsPageDto>> List(this CollectionsCaller caller, PactInstalmentsAppService service,
        string? view = null, decimal? minAmount = 100m, int page = 1, int pageSize = 25) =>
        service.ListAsync(caller, null, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), "outstanding", minAmount, null, page, pageSize, default, view);
}

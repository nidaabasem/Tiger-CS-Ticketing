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

    private static (PactInstalmentsAppService Service, FakeInstalmentSource Source) Build(DateTime? now = null)
    {
        now ??= Now;
        var source = new FakeInstalmentSource(now.Value);
        var options = new CollectionsOptions { Enabled = true };
        return (new PactInstalmentsAppService(options, new PactReceivablesOptions { Enabled = true }, new(options, new FakeDepartmentRepository()), new(options, new FakeTimeProvider(now.Value)),
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
        foreach (var min in new decimal?[] { 0m })   // the unit view has no per-instalment minimum (its minimum is on the unit total)
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
    public async Task TheUnitView_ClassifiesByTheCurrentMonth_NotTheDay()
    {
        var (service, source) = Build();   // today (Dubai) = 14 Oct 2026
        source.Rows.AddRange([OnDay("A", 1, "TP124-1", new DateTime(2026, 9, 30), 90, "V0"),     // last day of the previous month: Overdue
            OnDay("A", 1, "TP124-1", new DateTime(2026, 10, 1), 100, "V1"),                      // first day of the month: Due
            OnDay("A", 1, "TP124-1", new DateTime(2026, 10, 13), 110, "V2"),                     // yesterday: Due (this month)
            OnDay("A", 1, "TP124-1", new DateTime(2026, 10, 14), 120, "V3"),                     // today: Due
            OnDay("A", 1, "TP124-1", new DateTime(2026, 10, 31), 130, "V4"),                     // a coming day of this month: Due
            OnDay("A", 1, "TP124-1", new DateTime(2026, 11, 1), 140, "V5")]);                    // first day of next month: in neither
        var page = (await service.ListAsync(Agent, null, new DateOnly(2000, 1, 1), new DateOnly(2099, 12, 31), "outstanding", 0m, null, 1, 25, default, "units")).Value!;
        Assert.Equal((1, 4, 0), (page.Totals.OverdueCount, page.Totals.DueCount, page.Totals.NotYetDueCount));
        Assert.Equal((90m, 460m, 0m), (page.Totals.OverdueRemaining, page.Totals.DueRemaining, page.Totals.NotYetDueRemaining));
        Assert.Equal(new DateOnly(2026, 10, 31), page.DateTo);                                    // the window ends with the current month
        var unit = Assert.Single(page.Units!);
        Assert.Equal(["Due", "Due", "Due", "Due", "Overdue"], unit.Instalments.Select(i => i.Classification));      // newest due date first
        Assert.Equal(unit.RemainingTotal, unit.Instalments.Sum(i => i.RemainingAmount));          // the row and its instalments are the same rows
        Assert.Equal(550m, unit.RemainingTotal);                                                  // Total = Due + Overdue; 1 Nov is not in it
        Assert.True(source.LastRequest!.ClassifyByDay);
        Assert.Equal(new DateOnly(2026, 10, 14), source.LastRequest.AsOf);
        Assert.Null(page.Months);                                                                 // no month overview in this view
        // The instalment view keeps the month rule.
        var flat = (await service.ListAsync(Agent, null, new DateOnly(2000, 1, 1), new DateOnly(2099, 12, 31), "outstanding", 0m, null, 1, 25, default, "instalments")).Value!;
        Assert.False(source.LastRequest.ClassifyByDay);
        Assert.Equal(6, flat.Totals.Count);   // the instalment view still lists everything in its window
    }

    [Fact]
    public async Task TheDecemberToJanuaryBoundary_Overdue_IsBeforeDecember_AndJanuaryIsExcluded()
    {
        var (service, source) = Build(new DateTime(2026, 12, 20, 8, 0, 0, DateTimeKind.Utc));   // today (Dubai) = 20 Dec 2026
        source.Rows.AddRange([OnDay("A", 1, "TP124-1", new DateTime(2026, 11, 30), 10, "N"), OnDay("A", 1, "TP124-1", new DateTime(2026, 12, 1), 20, "D1"),
            OnDay("A", 1, "TP124-1", new DateTime(2026, 12, 31), 40, "D2"), OnDay("A", 1, "TP124-1", new DateTime(2027, 1, 1), 80, "J")]);
        var page = (await Units(service, to: new DateOnly(2099, 12, 31), minTotal: null)).Value!;
        Assert.Equal(new DateOnly(2026, 12, 31), page.DateTo);
        Assert.Equal((10m, 60m, 70m), (page.Totals.OverdueRemaining, page.Totals.DueRemaining, page.Totals.RemainingTotal));      // 1 Jan 2027 is in neither
        Assert.Equal(["D2", "D1", "N"], Assert.Single(page.Units!).Instalments.Select(i => i.VoucherNumber));
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

    private static Task<CollectionsResult<TigerCS.Application.Modules.Collections.Dto.PactInstalmentsPageDto>> Units(PactInstalmentsAppService service,
        DateOnly? from = null, DateOnly? to = null, decimal? minTotal = null, string? search = null, string? status = null, int pageSize = 25, int page = 1) =>
        service.ListAsync(Agent, null, from ?? new DateOnly(2000, 1, 1), to ?? new DateOnly(2099, 12, 31), "outstanding", null, search, page, pageSize, default, "units", null, status, minTotal);

    [Fact]
    public async Task MinimumTotal_IsOnTheUnitsSum_StrictlyGreater_AndCountsPagesAndTotalsFollowIt()
    {
        var (service, source) = Build();   // today (Dubai) = 14 Oct 2026
        source.Rows.AddRange([OnDay("A", 1, "TP124-1", new DateTime(2026, 10, 1), 60, "A1"), OnDay("A", 1, "TP124-1", new DateTime(2026, 10, 2), 60, "A2"),   // 120 from two small instalments
            OnDay("B", 2, "TP124-2", new DateTime(2026, 10, 1), 100, "B1"),                                                                                   // exactly 100
            OnDay("C", 3, "TP124-3", new DateTime(2026, 10, 1), 50, "C1"), OnDay("C", 3, "TP124-3", new DateTime(2026, 10, 3), 50, "C2"),                     // 50 + 50 = 100
            OnDay("D", 4, "TP124-4", new DateTime(2026, 10, 1), 250, "D1")]);
        var shown = (await Units(service, minTotal: 100m)).Value!;
        Assert.Equal(["TP124-1", "TP124-4"], shown.Units!.Select(u => u.UnitCode).Order());                     // 120 shows; 100 (one instalment or two) does not
        Assert.Equal((2, 3, 370m), (shown.Totals.UnitCount, shown.Totals.Count, shown.Totals.RemainingTotal)); // the counts and totals are those of the filtered units
        Assert.Equal(["TP124-1", "TP124-2", "TP124-3", "TP124-4"], (await Units(service, minTotal: null)).Value!.Units!.Select(u => u.UnitCode).Order());   // cleared: every unit with something due
        Assert.Equal(4, (await Units(service, minTotal: 99.99m)).Value!.Totals.UnitCount);
        Assert.Empty((await Units(service, minTotal: 250m)).Value!.Units!);                                       // 250 is not greater than 250
        var paged = (await Units(service, minTotal: 100m, pageSize: 1, page: 2)).Value!;                          // paging happens after the filter
        Assert.Equal(("TP124-4", 2), (Assert.Single(paged.Units!).UnitCode, paged.Totals.UnitCount));
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await Units(service, minTotal: -1m)).Outcome);
    }

    [Fact]
    public async Task EveryUnitsDetailsAddUpToItsTotal_AndTheStatusIsAgainstTheCurrentMonthWhateverMonthIsChosen()
    {
        var (service, source) = Build();
        source.Rows.AddRange([OnDay("A", 1, "TP124-1", new DateTime(2026, 8, 10), 300, "V1"), OnDay("A", 1, "TP124-1", new DateTime(2026, 9, 10), 120, "V2"),
            OnDay("A", 1, "TP124-1", new DateTime(2026, 10, 14), 80, "V3"), OnDay("A", 1, "TP124-1", new DateTime(2026, 10, 20), 999, "V4"),     // 20 Oct: a coming day of this month, Due
            OnDay("A", 1, "TP124-1", new DateTime(2026, 11, 20), 777, "V6"),                                                                    // next month: in neither
            OnDay("A", 2, "TP124-2", new DateTime(2026, 9, 11), 500, "V5")]);                                                                   // the same customer's other apartment
        var all = (await Units(service, minTotal: null)).Value!;
        var one = all.Units!.Single(u => u.UnitCode == "TP124-1");
        Assert.Equal(["V4", "V3", "V2", "V1"], one.Instalments.Select(i => i.VoucherNumber));                   // newest first; next month's V6 is not there
        Assert.Equal(1499m, one.RemainingTotal); Assert.Equal(one.RemainingTotal, one.Instalments.Sum(i => i.RemainingAmount));
        Assert.Equal(500m, all.Units!.Single(u => u.UnitCode == "TP124-2").RemainingTotal);                      // never mixed with TP124-1
        Assert.Equal(all.Totals.RemainingTotal, all.Units!.Sum(u => u.RemainingTotal));
        Assert.Equal(["Due", "Due", "Overdue", "Overdue"], one.Instalments.Select(i => i.Classification));

        // A previous month: only that month's instalments, still Overdue (compared with the current month, not with the month shown).
        var september = (await Units(service, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), null)).Value!;
        Assert.Equal(["V2", "V5"], september.Units!.SelectMany(u => u.Instalments).Select(i => i.VoucherNumber).Order());
        Assert.Equal((620m, 2, 0), (september.Totals.OverdueRemaining, september.Totals.OverdueCount, september.Totals.DueCount));
        // The current month: Due is the WHOLE month, the coming 20th included, and the window ends with the month.
        var october = (await Units(service, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), null)).Value!;
        var inOctober = Assert.Single(october.Units!).Instalments;
        Assert.Equal([("V4", "Due"), ("V3", "Due")], inOctober.Select(i => (i.VoucherNumber, i.Classification)));
        Assert.Equal(new DateOnly(2026, 10, 31), october.DateTo);
        // Next month is outside the rule: nothing to list, and the service refuses the window instead of reading it.
        var november = await Units(service, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), null);
        Assert.Equal(CollectionsOutcome.InvalidRequest, november.Outcome);
    }

    [Fact]
    public async Task ACancelledApartment_IsExcludedByItsUnitCode_NotByTheCustomer()
    {
        var (service, source) = Build();
        source.Rows.AddRange([OnDay("A", 1, "TP124-513*", new DateTime(2026, 9, 1), 900, "X1"), OnDay("A", 2, "TP124-514", new DateTime(2026, 9, 1), 400, "X2"),
            OnDay("B", 3, "TP124-*9", new DateTime(2026, 9, 1), 700, "X3"), OnDay("C", 4, "TP124-515", new DateTime(2026, 9, 1), 200, "X4")]);
        var page = (await Units(service, minTotal: null)).Value!;
        Assert.Equal(["TP124-514", "TP124-515"], page.Units!.Select(u => u.UnitCode).Order());                   // the customer's other apartment stays
        Assert.Equal((2, 2, 600m), (page.Totals.UnitCount, page.Totals.Count, page.Totals.RemainingTotal));      // the count and the totals never saw the cancelled ones
        Assert.Empty((await Units(service, minTotal: null, search: "513")).Value!.Units!);
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

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

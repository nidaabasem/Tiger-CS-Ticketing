using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Snapshot;

/// <summary>Receivables month overview: summaries cover the whole filtered set before paging, a selected month only narrows the list, Overdue never changes.</summary>
public sealed class ReceivablesMonthOverviewTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);   // business month = October 2026
    private static readonly CollectionsCaller Agent = new(Guid.NewGuid(), [Roles.CsAgent], []);

    private static (PactInstalmentsAppService Service, FakeInstalmentSource Source) Build()
    {
        var source = new FakeInstalmentSource(Now);
        var options = new CollectionsOptions { Enabled = true };
        var service = new PactInstalmentsAppService(options, new PactReceivablesOptions { Enabled = true }, new(options, new FakeDepartmentRepository()), new(options, new FakeTimeProvider(Now)),
            source, NullLogger<PactInstalmentsAppService>.Instance);
        // 6 units x months 3..12; each instalment 200 + unit.
        for (var u = 1; u <= 6; u++)
            for (var m = 3; m <= 12; m++)
                source.Rows.Add(new(4, "T" + u, "Customer " + u, u, "TP124-" + u, "V" + m, new DateTime(2026, m, 5), 400 + u, 200, 200 + u, "Installment", true));
        return (service, source);
    }

    private static Task<CollectionsResult<PactInstalmentsPageDto>> List(PactInstalmentsAppService service, string view, string? dueMonth = null, int page = 1, int pageSize = 25) =>
        service.ListAsync(Agent, null, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), "outstanding", 100m, null, page, pageSize, default, view, dueMonth);

    // The month overview belongs to the instalment view: the By unit view classifies by day and has no month cards.
    [Theory]
    [InlineData("instalments")]
    public async Task MonthSummaries_CoverTheWholeFilteredSet_RegardlessOfPaging(string view)
    {
        var (service, _) = Build();
        var small = (await List(service, view, pageSize: 2)).Value!;
        var large = (await List(service, view, pageSize: 100)).Value!;
        Assert.Equal(large.Months, small.Months);                                         // not computed from the visible page
        Assert.Equal(10, small.Months!.Count);                                            // Mar..Dec
        Assert.Equal(small.Totals.Count, small.Months.Sum(m => m.InstalmentCount));       // the cards add up to the list totals
        Assert.Equal(small.Totals.RemainingTotal, small.Months.Sum(m => m.RemainingTotal));
        Assert.Equal(small.Totals.OverdueCount, small.Months.Sum(m => m.OverdueCount));
        Assert.Equal(small.Totals.OverdueRemaining, small.Months.Sum(m => m.OverdueRemaining));
    }

    [Fact]
    public async Task Overdue_IsDecidedByTheBusinessMonth_NotBySelectingAMonth()
    {
        var (service, _) = Build();
        var all = (await List(service, "instalments")).Value!;
        Assert.All(all.Months!.Where(m => m.Month < 10), m => Assert.Equal(m.InstalmentCount, m.OverdueCount));   // before October 2026: overdue
        Assert.All(all.Months!.Where(m => m.Month >= 10), m => Assert.Equal(0, m.OverdueCount));                  // current and later months: never overdue
        foreach (var month in all.Months!)
        {
            var picked = (await List(service, "instalments", $"2026-{month.Month:00}", pageSize: 100)).Value!;
            Assert.Equal(month.OverdueCount, picked.Totals.OverdueCount);                  // same rule with or without a selection
            Assert.All(picked.Items, i => Assert.Equal(month.Month < 10 ? "Overdue" : month.Month == 10 ? "Due" : "NotYetDue", i.Classification));
        }
    }

    [Theory]
    [InlineData("instalments")]
    public async Task ASelectedMonth_NarrowsTheListAndTotals_ToExactlyThatMonthsCard(string view)
    {
        var (service, _) = Build();
        var all = (await List(service, view)).Value!;
        foreach (var card in all.Months!)
        {
            var picked = (await List(service, view, $"{card.Year}-{card.Month:00}", pageSize: 100)).Value!;
            Assert.Equal(all.Months, picked.Months);                                        // the overview is unchanged by the selection
            Assert.Equal(card.InstalmentCount, picked.Totals.Count);                        // card totals == filtered totals
            Assert.Equal(card.RemainingTotal, picked.Totals.RemainingTotal);
            Assert.Equal(card.OverdueCount, picked.Totals.OverdueCount);
            Assert.Equal(card.OverdueRemaining, picked.Totals.OverdueRemaining);
            var rows = view == "units" ? picked.Units!.SelectMany(u => u.Instalments).ToList() : picked.Items.ToList();
            Assert.Equal(card.InstalmentCount, rows.Count);                                 // ... and the listed rows
            Assert.Equal(card.RemainingTotal, rows.Sum(r => r.RemainingAmount));
            Assert.All(rows, r => Assert.Equal((card.Year, card.Month), (r.DueDate.Year, r.DueDate.Month)));
            Assert.Equal(card.Month.ToString("00"), picked.DueMonth![5..]);
        }
    }

    [Fact]
    public async Task AllMonths_IsTheUnselectedList_AndBadSelectionsAreRefused()
    {
        var (service, _) = Build();
        var none = (await List(service, "instalments")).Value!;
        Assert.Null(none.DueMonth);
        Assert.Equal(60, none.Totals.Count);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await List(service, "units", "2026-13")).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await List(service, "units", "soon")).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await List(service, "units", "2027-01")).Outcome);   // outside the selected dates
    }
}

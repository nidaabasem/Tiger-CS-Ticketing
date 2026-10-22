using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.Collections.Services;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Snapshot;

/// <summary>
/// Receivables and Campaigns share ONE Due / Overdue rule, against the current Dubai calendar month (not the day):
/// Overdue = DueDate &lt; monthStart; Due = DueDate &gt;= monthStart AND DueDate &lt; nextMonthStart; later months are in neither.
/// The amounts are the REMAINING amounts of unpaid or partly paid instalments; fully paid ones never count. Month/Year narrows the results only.
/// </summary>
public sealed class DueMonthRuleTests
{
    private static readonly CollectionsCaller Agent = new(Guid.NewGuid(), [Roles.CsAgent], []);
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    // ---- Receivables (unit view) ----
    private static (PactInstalmentsAppService Service, FakeInstalmentSource Source) Receivables(DateTime nowUtc)
    {
        var source = new FakeInstalmentSource(nowUtc);
        var options = new CollectionsOptions { Enabled = true };
        return (new PactInstalmentsAppService(options, new PactReceivablesOptions { Enabled = true }, new(options, new FakeDepartmentRepository()), new(options, new FakeTimeProvider(nowUtc)),
            source, NullLogger<PactInstalmentsAppService>.Instance), source);
    }

    /// <summary>An instalment with an original amount and the part already allocated (paid); the remaining amount is the difference.</summary>
    private static FakeInstalment Instalment(string tenant, int unit, DateTime due, decimal original, decimal paid, string voucher) =>
        new(4, tenant, "Customer " + tenant, unit, "TP124-" + unit, voucher, due, original, paid, original - paid, original - paid == 0 ? "Paid" : "Installment", true, "+971500000" + unit, tenant + "@example.test");

    private static Task<CollectionsResult<TigerCS.Application.Modules.Collections.Dto.PactInstalmentsPageDto>> Units(PactInstalmentsAppService service,
        DateOnly? from = null, DateOnly? to = null, string? status = null, decimal? minTotal = null) =>
        service.ListAsync(Agent, null, from ?? D(2000, 1, 1), to ?? D(2099, 12, 31), "outstanding", null, null, 1, 25, default, "units", null, status, minTotal);

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(31)]
    public async Task Receivables_TheBoundariesAreTheSame_WhicheverDayOfTheMonthTodayIs(int dayOfToday)
    {
        var (service, source) = Receivables(new DateTime(2026, 10, dayOfToday, 8, 0, 0, DateTimeKind.Utc));
        source.Rows.AddRange([Instalment("A", 1, new(2026, 9, 30), 100, 0, "S30"), Instalment("A", 1, new(2026, 10, 1), 200, 0, "O01"), Instalment("A", 1, new(2026, 10, 12), 300, 0, "O12"),
            Instalment("A", 1, new(2026, 10, 31), 400, 0, "O31"), Instalment("A", 1, new(2026, 11, 1), 800, 0, "N01")]);
        var page = (await Units(service, minTotal: null)).Value!;
        var unit = Assert.Single(page.Units!);
        Assert.Equal([("O31", "Due"), ("O12", "Due"), ("O01", "Due"), ("S30", "Overdue")], unit.Instalments.Select(i => (i.VoucherNumber, i.Classification)));
        Assert.Equal((900m, 100m, 1000m), (page.Totals.DueRemaining, page.Totals.OverdueRemaining, page.Totals.RemainingTotal));      // 1 Nov (800) is in neither
        Assert.Equal(D(2026, 10, 31), page.DateTo);
    }

    [Fact]
    public async Task Receivables_DueIsNotZeroWhenOnlyTheRestOfTheMonthIsOwed_EvenWithNoMinimumTotal()
    {
        // The reported symptom: Due = 0 with Minimum Total = 0 although instalments are due later in the same month.
        var (service, source) = Receivables(new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc));
        source.Rows.AddRange([Instalment("A", 1, new(2026, 10, 20), 250, 0, "L20"), Instalment("A", 1, new(2026, 10, 31), 150, 0, "L31")]);
        var all = (await Units(service, minTotal: 0m)).Value!;
        Assert.Equal((400m, 0m), (all.Totals.DueRemaining, all.Totals.OverdueRemaining));
        var due = (await Units(service, status: "due", minTotal: null)).Value!;
        Assert.Equal("TP124-1", Assert.Single(due.Units!).UnitCode);                                       // the Due filter finds a unit whose only payments are still to come
        Assert.Equal(400m, due.Totals.DueRemaining);
        Assert.Empty((await Units(service, status: "overdue", minTotal: null)).Value!.Units!);
    }

    [Fact]
    public async Task Receivables_PartlyPaidCountsItsRemainder_AndFullyPaidNeverCounts()
    {
        var (service, source) = Receivables(new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc));
        source.Rows.AddRange([
            Instalment("A", 1, new(2026, 9, 10), 1000, 400, "P-OVER"),       // partly paid, before the month: Overdue for the 600 left
            Instalment("A", 1, new(2026, 10, 25), 500, 100, "P-DUE"),        // partly paid, a coming day of the month: Due for the 400 left
            Instalment("A", 1, new(2026, 8, 1), 700, 700, "PAID-OLD"),       // fully paid long ago: nothing remains
            Instalment("A", 1, new(2026, 10, 5), 300, 300, "PAID-NOW"),      // fully paid this month: nothing remains
            Instalment("B", 2, new(2026, 10, 3), 900, 900, "PAID-ONLY")]);   // a unit with nothing left is not listed at all
        var page = (await Units(service, minTotal: null)).Value!;
        var unit = Assert.Single(page.Units!);
        Assert.Equal("TP124-1", unit.UnitCode);
        Assert.Equal([("P-DUE", 400m, "Due"), ("P-OVER", 600m, "Overdue")], unit.Instalments.Select(i => (i.VoucherNumber, i.RemainingAmount, i.Classification)));
        Assert.Equal((400m, 600m, 1000m), (page.Totals.DueRemaining, page.Totals.OverdueRemaining, page.Totals.RemainingTotal));
    }

    [Fact]
    public async Task Receivables_TheMonthFilterNarrowsTheResults_ButNeverMovesTheReferenceMonth()
    {
        var (service, source) = Receivables(new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc));
        source.Rows.AddRange([Instalment("A", 1, new(2026, 8, 10), 100, 0, "AUG"), Instalment("A", 1, new(2026, 9, 10), 200, 0, "SEP"),
            Instalment("A", 1, new(2026, 10, 3), 300, 0, "OCT3"), Instalment("A", 1, new(2026, 10, 28), 400, 0, "OCT28")]);
        var all = (await Units(service, minTotal: null)).Value!;                                            // All months: history + the whole current month
        Assert.Equal((700m, 300m), (all.Totals.DueRemaining, all.Totals.OverdueRemaining));
        var august = (await Units(service, D(2026, 8, 1), D(2026, 8, 31), minTotal: null)).Value!;           // a past month: still Overdue (against October)
        Assert.Equal((0m, 100m), (august.Totals.DueRemaining, august.Totals.OverdueRemaining));
        var october = (await Units(service, D(2026, 10, 1), D(2026, 10, 31), minTotal: null)).Value!;       // the current month: all of it Due, the 28th included
        Assert.Equal((700m, 0m), (october.Totals.DueRemaining, october.Totals.OverdueRemaining));
        Assert.Equal(["OCT28", "OCT3"], Assert.Single(october.Units!).Instalments.Select(i => i.VoucherNumber));
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await Units(service, D(2026, 11, 1), D(2026, 11, 30), minTotal: null)).Outcome);   // next month: neither Due nor Overdue
    }

    [Fact]
    public async Task Receivables_DecemberToJanuary_TheNextMonthStartIsTheFirstOfJanuary()
    {
        var (december, decSource) = Receivables(new DateTime(2026, 12, 15, 8, 0, 0, DateTimeKind.Utc));
        decSource.Rows.AddRange([Instalment("A", 1, new(2026, 11, 30), 10, 0, "N30"), Instalment("A", 1, new(2026, 12, 1), 20, 0, "D01"),
            Instalment("A", 1, new(2026, 12, 31), 40, 0, "D31"), Instalment("A", 1, new(2027, 1, 1), 80, 0, "J01")]);
        var dec = (await Units(december, minTotal: null)).Value!;
        Assert.Equal((60m, 10m, 70m), (dec.Totals.DueRemaining, dec.Totals.OverdueRemaining, dec.Totals.RemainingTotal));
        Assert.Equal(D(2026, 12, 31), dec.DateTo);
        Assert.Equal(["D31", "D01", "N30"], Assert.Single(dec.Units!).Instalments.Select(i => i.VoucherNumber));

        var (january, janSource) = Receivables(new DateTime(2027, 1, 10, 8, 0, 0, DateTimeKind.Utc));
        janSource.Rows.AddRange([Instalment("A", 1, new(2026, 12, 31), 10, 0, "D31"), Instalment("A", 1, new(2027, 1, 1), 20, 0, "J01"),
            Instalment("A", 1, new(2027, 1, 31), 40, 0, "J31"), Instalment("A", 1, new(2027, 2, 1), 80, 0, "F01")]);
        var jan = (await Units(january, minTotal: null)).Value!;
        Assert.Equal((60m, 10m, 70m), (jan.Totals.DueRemaining, jan.Totals.OverdueRemaining, jan.Totals.RemainingTotal));          // 31 Dec is Overdue; 1 Feb is out
        Assert.Equal(D(2027, 1, 31), jan.DateTo);
    }

    // ---- Campaigns ----
    private static PactReceivableInstalment Pay(string tenant, int unit, DateTime due, decimal remaining, string voucher, string status = "Installment") =>
        new(4, tenant, "Customer " + tenant, "971500003001", "x@example.test", unit, $"TP140-{unit}", "TP140", voucher, "", due, remaining, status);

    private static CollectionsCampaignAppServiceTests.Harness Campaigns(DateTime? nowUtc = null)
    {
        var h = new CollectionsCampaignAppServiceTests.Harness(nowUtc); h.Campaign.FinancialSourceValidated = true;
        return h;
    }

    [Fact]
    public async Task Campaigns_ShareTheRule_RestOfTheMonthIsDue_NextMonthIsExcluded_AndTheReadWindowEndsWithTheMonth()
    {
        var h = Campaigns();   // today = 14 Oct 2026
        h.Source.Items.AddRange([Pay("A", 1, new(2026, 9, 30), 100m, "S30"), Pay("A", 1, new(2026, 10, 1), 200m, "O01"), Pay("A", 1, new(2026, 10, 14), 300m, "O14"),
            Pay("A", 1, new(2026, 10, 31), 400m, "O31"), Pay("A", 1, new(2026, 11, 1), 800m, "N01")]);
        var item = Assert.Single((await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", minTotal: null)).Value!.Items);
        Assert.Equal((900m, 100m, 1000m), (item.DueAmount, item.OverdueAmount, item.TotalAmount));
        Assert.Equal(new DateOnly(2026, 10, 31), h.Source.LastRequest!.ThroughDate);
        Assert.Equal(900m, item.Amount);                                                                 // the stage amount: 200 + 300 + 400, all of October (the 400 on the 31st is a coming day of the stage month)
        var csv = (await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "review", minTotal: null)).Value!.Csv;
        Assert.DoesNotContain("800", csv);                                                               // the 1 Nov instalment is not part of the send data
        Assert.Contains("\"900.00\"", csv);                                                              // the stage amount: all October instalments (200 + 300 + 400)
    }

    [Fact]
    public async Task Campaigns_AllMonthsIncludesHistoricOverdueAndTheWholeCurrentMonth_AMonthFilterOnlyNarrows()
    {
        var h = Campaigns();   // today = 14 Oct 2026; the Overdue stage is eligible on instalments older than a month
        h.Source.Items.AddRange([Pay("A", 1, new(2026, 8, 10), 300m, "AUG"), Pay("A", 1, new(2026, 10, 25), 100m, "OCT25")]);
        var all = Assert.Single((await h.Service.PreviewAsync(h.Manager, "OverdueReminder", minTotal: null)).Value!.Items);
        Assert.Equal((100m, 300m, 400m), (all.DueAmount, all.OverdueAmount, all.TotalAmount));          // the 25th of October is still to come: Due
        Assert.Equal(300m, all.Amount);                                                                  // the stage amount is unchanged: only what is older than a month
        var august = Assert.Single((await h.Service.PreviewAsync(h.Manager, "OverdueReminder", dateFrom: new(2026, 8, 1), dateTo: new(2026, 8, 31), minTotal: null)).Value!.Items);
        Assert.Equal((0m, 300m, 300m), (august.DueAmount, august.OverdueAmount, august.TotalAmount));   // narrowed to August: still Overdue against October, no Due
    }

    [Fact]
    public async Task Campaigns_PartlyPaidCountsItsRemainder_AndFullyPaidNeverCounts()
    {
        var h = Campaigns();
        h.Source.Items.AddRange([Pay("A", 1, new(2026, 9, 10), 600m, "P-OVER"),                           // partly paid (the source sends what remains)
            Pay("A", 1, new(2026, 10, 25), 400m, "P-DUE"),
            Pay("A", 1, new(2026, 10, 8), 250m, "P-MID"),                                                  // partly paid, up to today: what makes the unit a candidate
            Pay("A", 1, new(2026, 10, 5), 0m, "PAID", "Paid"),                                             // fully paid: nothing remains
            Pay("B", 2, new(2026, 10, 8), 0m, "PAID-B", "Paid")]);                                         // a unit with nothing left is not a candidate
        var preview = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", minTotal: null)).Value!;
        var item = Assert.Single(preview.Items);
        Assert.Equal(("A", 650m, 600m, 1250m), (item.TenantId, item.DueAmount, item.OverdueAmount, item.TotalAmount));
        Assert.Equal(650m, item.Amount);                                                                  // 400 (25 Oct) + 250 (8 Oct): the stage month; the September 600 is Overdue and not in this stage
    }

    [Fact]
    public async Task Campaigns_DecemberToJanuary()
    {
        var december = Campaigns(new DateTime(2026, 12, 15, 8, 0, 0, DateTimeKind.Utc));
        december.Source.Items.AddRange([Pay("A", 1, new(2026, 11, 30), 10m, "N30"), Pay("A", 1, new(2026, 12, 1), 20m, "D01"), Pay("A", 1, new(2026, 12, 31), 40m, "D31"),
            Pay("A", 1, new(2027, 1, 1), 80m, "J01")]);
        var dec = Assert.Single((await december.Service.PreviewAsync(december.Manager, "CurrentMonthReminder", minTotal: null)).Value!.Items);
        Assert.Equal((60m, 10m, 70m), (dec.DueAmount, dec.OverdueAmount, dec.TotalAmount));              // 1 Jan 2027 is in neither
        Assert.Equal(new DateOnly(2026, 12, 31), december.Source.LastRequest!.ThroughDate);

        var january = Campaigns(new DateTime(2027, 1, 10, 8, 0, 0, DateTimeKind.Utc));
        january.Source.Items.AddRange([Pay("A", 1, new(2026, 12, 31), 10m, "D31"), Pay("A", 1, new(2027, 1, 1), 20m, "J01"), Pay("A", 1, new(2027, 1, 31), 40m, "J31"),
            Pay("A", 1, new(2027, 2, 1), 80m, "F01")]);
        var jan = Assert.Single((await january.Service.PreviewAsync(january.Manager, "CurrentMonthReminder", minTotal: null)).Value!.Items);
        Assert.Equal((60m, 10m, 70m), (jan.DueAmount, jan.OverdueAmount, jan.TotalAmount));              // 31 Dec is Overdue; 1 Feb is out
        Assert.Equal(new DateOnly(2027, 1, 31), january.Source.LastRequest!.ThroughDate);
    }

    [Fact]
    public async Task OneUnit_ShowsTheSameDueOverdueAndTotal_OnReceivables_Campaigns_AndThePaymentSummary()
    {
        var now = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
        var due = new[] { (new DateTime(2026, 9, 30), 100m), (new DateTime(2026, 10, 1), 200m), (new DateTime(2026, 10, 12), 300m), (new DateTime(2026, 10, 31), 400m), (new DateTime(2026, 11, 1), 500m) };
        // Receivables
        var (receivables, source) = Receivables(now);
        source.Rows.AddRange(due.Select((d, n) => Instalment("A", 1, d.Item1, d.Item2, 0, "V" + n)));
        var unit = Assert.Single((await Units(receivables, minTotal: null)).Value!.Units!);
        // Campaigns
        var h = Campaigns(now);
        h.Source.Items.AddRange(due.Select((d, n) => Pay("A", 1, d.Item1, d.Item2, "V" + n)));
        var item = Assert.Single((await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", minTotal: null)).Value!.Items);
        // Payment Summary (also what the customer card and the Genesys unit details are built from)
        var status = ReceivablesSnapshotComposer.BuildStatus(D(2000, 1, 1), D(2026, 10, 12), [FakeSnapshotSource.Healthy(4, now.AddMinutes(-5))], [], now, 90);
        var summary = CollectionsUnitPaymentSummaryAppService.Compose("TP124-1", D(2026, 10, 12),
            new(status, [new PactUnitIdentity(4, "A", 1, "TP124-1", "Customer A", "", "", "124", "Tower", 5, 5)],
                due.Select((d, n) => new PactUnitInstalment(4, "A", 1, "V" + n, DateOnly.FromDateTime(d.Item1), d.Item2, d.Item2, 0m, "Installment")).ToList(),
                new Dictionary<int, CrmUnitLink>(), true, now), 90);
        Assert.Equal((900m, 100m, 1000m), (item.DueAmount, item.OverdueAmount, item.TotalAmount));
        Assert.Equal((900m, 100m, 1000m), (summary.Due, summary.Overdue, summary.Total));
        Assert.Equal(1000m, unit.RemainingTotal);
        Assert.Equal(900m, unit.Instalments.Where(i => i.Classification == "Due").Sum(i => i.RemainingAmount));
        Assert.Equal(100m, unit.Instalments.Where(i => i.Classification == "Overdue").Sum(i => i.RemainingAmount));
    }

    [Fact]
    public async Task Campaigns_AComingDayOfThisMonth_IsEligibleOnlyForTheWholeMonthStages_LikeReview()
    {
        var h = Campaigns();   // today = 14 Oct 2026
        h.Source.Items.Add(Pay("A", 1, new(2026, 10, 28), 500m, "O28"));
        // The stage window of Current-month and Follow-up is the whole month, so a coming day qualifies for them; the Overdue / Legal stages look at older months and are unaffected.
        foreach (var stage in new[] { "CurrentMonthReminder", "FollowUpReminder" })
            Assert.All((await h.Service.PreviewAsync(h.Manager, stage, minTotal: null)).Value!.Items, i => { Assert.NotEqual("NotEligible", i.Status); Assert.Equal((500m, 500m), (i.DueAmount, i.Amount ?? 0m)); });
        foreach (var stage in new[] { "OverdueReminder", "LegalNotice", "LegalReferral" })
            Assert.All((await h.Service.PreviewAsync(h.Manager, stage, minTotal: null)).Value!.Items, i => { Assert.Equal("NotEligible", i.Status); Assert.Equal(500m, i.DueAmount); });
        h.Source.Items.Add(Pay("A", 1, new(2026, 10, 9), 100m, "O09"));
        var item = Assert.Single((await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", minTotal: null)).Value!.Items);
        Assert.Equal(600m, item.Amount);                                                                  // the stage amount: 9 Oct and 28 Oct, the whole stage month
        Assert.Equal((600m, 0m, 600m), (item.DueAmount, item.OverdueAmount, item.TotalAmount));
    }

    [Fact]
    public void Campaigns_TheReminderScheduleAndStageRangesDoNotDependOnTheDefaultToDate()
    {
        // Timing is a function of the preview date alone (ScheduledDates) and eligibility of StageRange: neither takes the window's To.
        foreach (var stage in Enum.GetValues<TigerCS.Domain.Modules.Collections.CollectionsCampaignStage>())
            Assert.NotEmpty(TigerCS.Domain.Modules.Collections.CollectionsCampaignPolicy.ScheduledDates(stage, new DateOnly(2026, 10, 12)));
        var parameters = typeof(TigerCS.Domain.Modules.Collections.CollectionsCampaignPolicy).GetMethod(nameof(TigerCS.Domain.Modules.Collections.CollectionsCampaignPolicy.StageRange))!.GetParameters();
        Assert.Equal(["stage", "businessDate"], parameters.Select(p => p.Name));
    }
}

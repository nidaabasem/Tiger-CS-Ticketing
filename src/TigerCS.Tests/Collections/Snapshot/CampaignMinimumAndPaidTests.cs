using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualBasic.FileIO;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Snapshot;

/// <summary>Campaign rows: minimum outstanding amount in preview and both exports; fully paid instalments are never reminder candidates.</summary>
public sealed class CampaignMinimumAndPaidTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Returns every row it holds (including paid ones and sub-minimum ones) so the application-side rules are what is tested.</summary>
    private sealed class RawSource(List<PactReceivableInstalment> rows) : IPactReceivablesSource
    {
        public PactReceivablesRequest? Last { get; private set; }
        public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PactReceivablesSnapshot> ReadAsync(PactReceivablesRequest request, CancellationToken cancellationToken)
        {
            Last = request;
            var status = ReceivablesSnapshotComposer.BuildStatus(request.FromDate ?? new(2026, 1, 1), request.ThroughDate,
                [FakeSnapshotSource.Healthy(4, Now.AddMinutes(-5)), FakeSnapshotSource.Healthy(32, Now.AddMinutes(-5))], [], Now, 90);
            return Task.FromResult(new PactReceivablesSnapshot(rows, Now.AddMinutes(-5), false, status));
        }
    }

    private static CollectionsCampaignAppService Service(IPactReceivablesSource source, decimal defaultMin)
    {
        var options = new CollectionsOptions { Enabled = true };
        return new(options, new CollectionsCampaignOptions { FinancialSourceValidated = true }, new PactReceivablesOptions { Enabled = true, DefaultMinOutstandingAmount = defaultMin },
            new(options, new FakeDepartmentRepository()), new(options, new FakeTimeProvider(Now)), source, NullLogger<CollectionsCampaignAppService>.Instance);
    }

    private static PactReceivableInstalment Row(string tenant, int unit, DateTime due, decimal amount) =>
        new(4, tenant, "Customer " + tenant, "971500003001", "x@example.test", unit, "TP124-" + unit, "", "INV-" + tenant, "", due, amount, amount == 0 ? "Paid" : "Installment");

    private static readonly CollectionsCaller Manager = new(Guid.NewGuid(), [Roles.CsManager], []);

    [Fact]
    public async Task MinimumTotal_IsGreaterThan_OnTheUnitsSum_AndSharedByPreviewAndBothExports()
    {
        // Unit A owes 100 (two instalments of 50): not above 100 -> hidden. Unit B: 120 made of two small instalments -> shown. Unit C: one big instalment.
        var source = new RawSource([Row("A", 1, new(2026, 10, 3), 50m), Row("A", 1, new(2026, 10, 5), 50m),
            Row("B", 2, new(2026, 10, 3), 70m), Row("B", 2, new(2026, 10, 6), 50m), Row("C", 3, new(2026, 10, 10), 500m)]);
        var service = Service(source, 100m);
        var preview = (await service.PreviewAsync(Manager, "CurrentMonthReminder", minTotal: 100m)).Value!;
        Assert.Equal(["B", "C"], preview.Items.Select(i => i.TenantId));
        Assert.Equal(2, preview.TotalCount);                                                   // the count follows the filter
        Assert.Equal(0m, source.Last!.MinAmount);                                              // never applied to single instalments
        foreach (var mode in new[] { "review", "genesys" })
        {
            var file = (await service.ExportAsync(Manager, "CurrentMonthReminder", mode, minTotal: 100m)).Value!;
            Assert.Equal(preview.TotalCount, file.RowCount);
            Assert.Equal(["B", "C"], Tenants(file.Csv));
        }
        Assert.Equal(["A", "B", "C"], (await service.PreviewAsync(Manager, "CurrentMonthReminder", minTotal: null)).Value!.Items.Select(i => i.TenantId));   // cleared
        Assert.Equal(0, (await service.PreviewAsync(Manager, "CurrentMonthReminder", minTotal: 1000m)).Value!.TotalCount);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await service.PreviewAsync(Manager, "CurrentMonthReminder", minTotal: -1m)).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await service.ExportAsync(Manager, "CurrentMonthReminder", "review", minTotal: -1m)).Outcome);
    }

    [Fact]
    public async Task Pages_AreCutAfterTheMinimumTotal_SoCountsAndPagesAgree()
    {
        var source = new RawSource([.. Enumerable.Range(1, 12).Select(n => Row("S" + n, n, new(2026, 10, 3), 40m)),      // 40: hidden
            .. Enumerable.Range(13, 7).Select(n => Row("L" + n, n, new(2026, 10, 3), 400m))]);                             // 400: shown
        var service = Service(source, 0m);
        var first = (await service.PreviewAsync(Manager, "CurrentMonthReminder", minTotal: 100m, pageSize: 3)).Value!;
        Assert.Equal(7, first.TotalCount);
        Assert.Single((await service.PreviewAsync(Manager, "CurrentMonthReminder", minTotal: 100m, pageSize: 3, page: 3)).Value!.Items);   // 7 units = 3 + 3 + 1
        var all = new List<string>();
        for (var page = 1; page <= 3; page++) all.AddRange((await service.PreviewAsync(Manager, "CurrentMonthReminder", minTotal: 100m, pageSize: 3, page: page)).Value!.Items.Select(i => i.TenantId));
        Assert.Equal(7, all.Distinct().Count());
        Assert.All(all, t => Assert.StartsWith("L", t));
    }

    [Fact]
    public async Task FutureInstalments_NeverEnterTheListTheTotalsOrTheExport()
    {
        // Today is 14 Oct 2026 (Dubai). 20 Oct is in the future: only the 10 Oct instalment (Overdue) and the one due today count.
        var source = new RawSource([Row("A", 1, new(2026, 10, 10), 300m), Row("A", 1, new(2026, 10, 14), 200m), Row("A", 1, new(2026, 10, 20), 900m),
            Row("F", 2, new(2026, 10, 25), 900m)]);                                               // only a future instalment: not listed at all
        var service = Service(source, 0m);
        var preview = (await service.PreviewAsync(Manager, "CurrentMonthReminder", minTotal: null)).Value!;
        var item = Assert.Single(preview.Items);
        Assert.Equal((200m, 300m, 500m), (item.DueAmount, item.OverdueAmount, item.TotalAmount));
        Assert.Equal(new DateOnly(2026, 10, 14), source.Last!.ThroughDate);                       // the source is never even asked for later dates
        var csv = (await service.ExportAsync(Manager, "CurrentMonthReminder", "review", minTotal: null)).Value!.Csv;
        Assert.DoesNotContain("900", csv);                                                       // the future instalments are not in the file either
        Assert.Contains("\"500.00\"", csv);                                                      // the exported amount is the stage amount of the same (past + today) instalments
    }

    [Fact]
    public async Task TheMonthWindow_NarrowsTheUnitsAmounts_ToThatMonthsInstalments_UpToToday()
    {
        // Same unit: 3 Oct and 12 Oct are in October; 20 Oct is after today. Selecting October gives the same Due/Overdue as All months for this stage; another month is empty.
        var source = new RawSource([Row("A", 1, new(2026, 10, 3), 100m), Row("A", 1, new(2026, 10, 12), 150m), Row("A", 1, new(2026, 10, 20), 400m), Row("A", 1, new(2026, 9, 15), 70m)]);
        var service = Service(source, 0m);
        var october = (await service.PreviewAsync(Manager, "CurrentMonthReminder", dateFrom: new(2026, 10, 1), dateTo: new(2026, 10, 31), minTotal: null)).Value!;
        var item = Assert.Single(october.Items);
        Assert.Equal((0m, 250m, 250m), (item.DueAmount, item.OverdueAmount, item.TotalAmount));
        Assert.Equal(new DateOnly(2026, 10, 14), october.DateTo);                                 // capped at today (14 Oct), the preview shows the window it really used
        var september = (await service.PreviewAsync(Manager, "CurrentMonthReminder", dateFrom: new(2026, 9, 1), dateTo: new(2026, 9, 30), minTotal: null)).Value!;
        Assert.Empty(september.Items);                                                            // the stage's own eligibility (this month's instalments) is unchanged
        var future = await service.PreviewAsync(Manager, "CurrentMonthReminder", dateFrom: new(2026, 11, 1), dateTo: new(2026, 11, 30), minTotal: null);
        Assert.Equal(CollectionsOutcome.InvalidRequest, future.Outcome);                          // nothing is listed after today
    }

    [Fact]
    public async Task ACancelledApartment_IsExcludedByItsCode_AndTheCustomersOtherApartmentsStay()
    {
        var source = new RawSource([Row("A", 1, new(2026, 10, 3), 400m) with { UnitCode = "TP124-513*" }, Row("A", 2, new(2026, 10, 3), 250m),
            Row("B", 3, new(2026, 10, 3), 300m) with { UnitCode = "TP124-*7" }]);
        var service = Service(source, 0m);
        var preview = (await service.PreviewAsync(Manager, "CurrentMonthReminder", minTotal: null)).Value!;
        var item = Assert.Single(preview.Items);
        Assert.Equal(("A", "TP124-2", 250m), (item.TenantId, item.UnitCode, item.TotalAmount));
        Assert.Equal(1, preview.TotalCount);
        Assert.Equal(["A"], Tenants((await service.ExportAsync(Manager, "CurrentMonthReminder", "review", minTotal: null)).Value!.Csv));
    }

    [Fact]
    public async Task FullyPaidInstalments_AreNeverReminderCandidates_EvenIfTheSourceReturnsThem()
    {
        var source = new RawSource([Row("PAID", 1, new(2026, 10, 10), 0m), Row("OWES", 2, new(2026, 10, 12), 400m)]);
        var service = Service(source, 0m);
        var preview = (await service.PreviewAsync(Manager, "CurrentMonthReminder")).Value!;
        Assert.Equal("OWES", Assert.Single(preview.Items).TenantId);
        Assert.Equal("OWES", Assert.Single(Tenants((await service.ExportAsync(Manager, "CurrentMonthReminder", "review")).Value!.Csv)));
    }

    [Fact]
    public async Task EarliestUnpaidDueDate_IsTakenFromQualifyingInstalmentsWithARemainingBalance()
    {
        // One apartment: a paid instalment on 2 Oct (never counted), unpaid 8 Oct and 12 Oct -> the earliest UNPAID due date is 8 Oct.
        var source = new RawSource([Row("A", 1, new(2026, 10, 2), 0m), Row("A", 1, new(2026, 10, 8), 300m), Row("A", 1, new(2026, 10, 12), 200m)]);
        var item = Assert.Single((await Service(source, 0m).PreviewAsync(Manager, "CurrentMonthReminder")).Value!.Items);
        Assert.Equal(new DateOnly(2026, 10, 8), item.DueDate);
        Assert.Equal(500m, item.Amount);                                     // only the unpaid balances are summed
    }

    private static List<string> Tenants(string csv)
    {
        using var parser = new TextFieldParser(new StringReader(csv)) { Delimiters = [","], HasFieldsEnclosedInQuotes = true };
        parser.ReadFields();
        var list = new List<string>();
        while (!parser.EndOfData) list.Add(parser.ReadFields()![3]);
        return list;
    }
}

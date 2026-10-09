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
    public async Task Minimum_IsInclusive_AndSharedByPreviewAndBothExports()
    {
        var source = new RawSource([Row("A", 1, new(2026, 10, 20), 99.99m), Row("B", 2, new(2026, 10, 21), 100m), Row("C", 3, new(2026, 10, 22), 500m)]);
        var service = Service(source, 100m);
        var preview = (await service.PreviewAsync(Manager, "CurrentMonthReminder")).Value!;      // no minAmount sent: the default 100 applies
        Assert.Equal(["B", "C"], preview.Items.Select(i => i.TenantId));
        Assert.Equal(100m, preview.MinAmount); Assert.Equal(100m, source.Last!.MinAmount);
        foreach (var mode in new[] { "review", "genesys" })
        {
            var file = (await service.ExportAsync(Manager, "CurrentMonthReminder", mode, businessDate: null)).Value!;
            Assert.Equal(preview.TotalCount, file.RowCount);
            Assert.Equal(["B", "C"], Tenants(file.Csv));
        }
        Assert.Equal(["A", "B", "C"], (await service.PreviewAsync(Manager, "CurrentMonthReminder", minAmount: 0)).Value!.Items.Select(i => i.TenantId));
        Assert.Equal(0, (await service.PreviewAsync(Manager, "CurrentMonthReminder", minAmount: 1000m)).Value!.TotalCount);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await service.PreviewAsync(Manager, "CurrentMonthReminder", minAmount: -1m)).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await service.ExportAsync(Manager, "CurrentMonthReminder", "review", minAmount: -1m)).Outcome);
    }

    [Fact]
    public async Task Minimum_ReachesTheSourceAndTheExportRequest_WithTheSameValue()
    {
        var source = new RawSource([Row("A", 1, new(2026, 10, 20), 750m)]);
        var service = Service(source, 100m);
        await service.PreviewAsync(Manager, "CurrentMonthReminder", minAmount: 250.5m);
        Assert.Equal(250.5m, source.Last!.MinAmount);
        await service.ExportAsync(Manager, "CurrentMonthReminder", "review", minAmount: 250.5m);
        Assert.Equal(250.5m, source.Last!.MinAmount);
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
        // One apartment: a paid instalment on 2 Oct (never counted), unpaid 20 Oct and 25 Oct -> the earliest UNPAID due date is 20 Oct.
        var source = new RawSource([Row("A", 1, new(2026, 10, 2), 0m), Row("A", 1, new(2026, 10, 20), 300m), Row("A", 1, new(2026, 10, 25), 200m)]);
        var item = Assert.Single((await Service(source, 0m).PreviewAsync(Manager, "CurrentMonthReminder")).Value!.Items);
        Assert.Equal(new DateOnly(2026, 10, 20), item.DueDate);
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

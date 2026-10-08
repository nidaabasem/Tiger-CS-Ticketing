using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualBasic.FileIO;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Services;

public sealed class CollectionsCampaignAppServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
    private sealed class Source : IPactReceivablesSource
    {
        public List<PactReceivableInstalment> Items { get; } = [];
        public int Reads { get; private set; }
        public DateTime ReadAt { get; set; } = Now;
        public Exception? Failure { get; set; }
        public Task<PactReceivablesSnapshot> ReadAsync(DateOnly throughDate, CancellationToken cancellationToken)
        {
            Reads++;
            if (Failure is not null) throw Failure;
            Assert.Equal(new DateOnly(throughDate.Year, throughDate.Month, DateTime.DaysInMonth(throughDate.Year, throughDate.Month)), throughDate);
            return Task.FromResult(new PactReceivablesSnapshot(Items, ReadAt, false));
        }
    }

    private sealed class Harness
    {
        public Source Source { get; } = new();
        public CollectionsOptions Options { get; } = new() { Enabled = true };
        public PactReceivablesOptions Sql { get; } = new() { Enabled = true };
        public CollectionsCampaignOptions Campaign { get; } = new();
        public CollectionsCaller Manager { get; } = new(Guid.NewGuid(), [Roles.CsManager], []);
        public CollectionsCaller Agent { get; } = new(Guid.NewGuid(), [Roles.CsAgent], []);
        public CollectionsCampaignAppService Service { get; }
        public Harness() => Service = new(Options, Campaign, Sql,
            new(Options, new FakeDepartmentRepository()), new(Options, new FakeTimeProvider(Now)),
            Source, NullLogger<CollectionsCampaignAppService>.Instance);
    }

    private static PactReceivableInstalment Row(int day = 20, decimal amount = 500m, string tenant = "3001",
        int company = 4, int unit = 101) => new(company, tenant, "Example Customer", "971500003001", "example@example.test",
            unit, $"TP140-{unit}", "TP140", "INV-1", "", new(2026, 10, day), amount, "Installment");

    [Fact]
    public async Task DefaultIsReviewOnly_GenesysExportRequiresReconciledSource()
    {
        var h = new Harness(); h.Source.Items.Add(Row());
        var report = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder")).Value!;
        var row = Assert.Single(report.Items);
        Assert.Equal("NeedsReview", row.Status);
        Assert.Contains("SourceReconciliationRequired", row.Reason);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "genesys")).Outcome);
        var review = (await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "review")).Value!;
        Assert.Contains("InternalReviewOnly", review.Csv);
        Assert.Contains("SourceReconciliationRequired", review.Csv);
    }

    [Fact]
    public async Task ExportReReadsAllPages_AndAlreadyPaidUnitDisappears()
    {
        var h = new Harness(); h.Campaign.FinancialSourceValidated = true;
        for (var n = 1; n <= 30; n++) h.Source.Items.Add(Row(tenant: n.ToString()));
        var preview = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder")).Value!;
        Assert.Equal(25, preview.Items.Count); Assert.Equal(30, preview.TotalCount);
        var stableId = preview.Items.First(c => c.TenantId == "10").RecordId;
        h.Source.Items.RemoveAll(r => r.TenantId == "1");
        var file = (await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "genesys")).Value!;
        Assert.Equal(29, file.RowCount); Assert.Equal(2, h.Source.Reads);
        Assert.Contains(stableId, file.Csv);
        using var parser = new TextFieldParser(new StringReader(file.Csv)) { Delimiters = [","], HasFieldsEnclosedInQuotes = true };
        Assert.Equal(22, parser.ReadFields()!.Length);
        var tenants = new List<string>();
        while (!parser.EndOfData) { var cells = parser.ReadFields()!; tenants.Add(cells[3]); Assert.Equal("+971500003001", cells[5]); Assert.Equal(new[] { "true", "true", "true" }, cells[19..]); }
        Assert.Equal(29, tenants.Count); Assert.DoesNotContain("1", tenants);
    }

    [Fact]
    public async Task UnitsAndCompaniesAreIndependent_IdsAreStableWithinTheMonth()
    {
        var h = new Harness(); h.Campaign.FinancialSourceValidated = true;
        h.Source.Items.AddRange([Row(amount: 100m), Row(day: 21, amount: 200m, unit: 202), Row(amount: 300m, company: 32)]);
        var items = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder")).Value!.Items;
        Assert.Equal(3, items.Count); Assert.Equal(3, items.Select(c => c.RecordId).Distinct().Count());
        Assert.Equal(new decimal?[] { 100m, 200m, 300m }, items.Select(c => c.Amount));
        var again = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", new(2026, 10, 15))).Value!.Items;
        Assert.Equal(items.Select(c => c.RecordId), again.Select(c => c.RecordId));
        Assert.All(again, c => Assert.Equal("PreviewOnly", c.Status));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidUnitCannotEnterAGenesysCampaign(int unit)
    {
        var h = new Harness(); h.Campaign.FinancialSourceValidated = true; h.Source.Items.Add(Row(unit: unit));
        Assert.Contains("MissingUnitIdentity", Assert.Single((await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder")).Value!.Items).Reason);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "genesys")).Outcome);
    }

    [Fact]
    public async Task RepeatedDueDatesAcrossUnitsAreFlaggedEvenAfterSourceValidation()
    {
        var h = new Harness(); h.Campaign.FinancialSourceValidated = true;
        h.Source.Items.AddRange([Row(), Row(unit: 202)]);
        Assert.All((await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder")).Value!.Items,
            c => Assert.Contains("UnitAllocationNeedsReview", c.Reason));
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "genesys")).Outcome);
    }

    [Fact]
    public async Task StaleSourceFailsCampaignExport_AndSourceFailureDoesNotLookLikeAnEmptyList()
    {
        var h = new Harness(); h.Campaign.FinancialSourceValidated = true; h.Source.Items.Add(Row());
        h.Source.ReadAt = Now.AddHours(-2);
        Assert.Contains("StaleSource", Assert.Single((await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder")).Value!.Items).Reason);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "genesys")).Outcome);
        h.Source.Failure = new PactReceivablesSourceException("Unavailable");
        var failed = await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder");
        Assert.Equal(CollectionsOutcome.FinanceUnavailable, failed.Outcome); Assert.Null(failed.Value);
    }

    [Fact]
    public async Task LegalNoticeIsSeparateAndReferralNeverBecomesACustomerCampaign()
    {
        var h = new Harness(); h.Campaign.FinancialSourceValidated = true;
        h.Source.Items.Add(Row(amount: 2000m) with { DueDate = new(2026, 9, 20) });
        Assert.Contains("LegalNoticeReleaseRequired", Assert.Single((await h.Service.PreviewAsync(h.Manager, "LegalNotice")).Value!.Items).Reason);
        h.Campaign.LegalNoticeExportEnabled = true;
        Assert.Equal(CollectionsOutcome.Success, (await h.Service.ExportAsync(h.Manager, "LegalNotice", "genesys")).Outcome);
        h.Source.Items.Clear(); h.Source.Items.Add(Row(amount: 25000m) with { DueDate = new(2026, 6, 1) });
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ExportAsync(h.Manager, "LegalReferral", "genesys", new(2026, 10, 30))).Outcome);
        Assert.Equal(CollectionsOutcome.Success, (await h.Service.ExportAsync(h.Manager, "LegalReferral", "review")).Outcome);
    }

    [Fact]
    public async Task ExportCapRejectsRatherThanTruncating()
    {
        var h = new Harness(); h.Campaign.MaxExportRows = 1;
        h.Source.Items.AddRange([Row(), Row(tenant: "3002")]);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "review")).Outcome);
    }

    [Fact]
    public async Task AgentsCanPreviewButCannotExport_AndInvalidRequestsNeverReadSource()
    {
        var h = new Harness(); h.Source.Items.Add(Row());
        Assert.Equal(CollectionsOutcome.Success, (await h.Service.PreviewAsync(h.Agent, "CurrentMonthReminder")).Outcome);
        var reads = h.Source.Reads;
        Assert.Equal(CollectionsOutcome.Forbidden, (await h.Service.ExportAsync(h.Agent, "CurrentMonthReminder", "review")).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.PreviewAsync(h.Manager, "1")).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", companyId: 25)).Outcome);
        Assert.Equal(reads, h.Source.Reads);
    }

    [Fact]
    public async Task ReviewCsvEscapesCommasQuotesNewlinesAndFormulaPrefixes()
    {
        var h = new Harness(); h.Source.Items.Add(Row() with { FullName = "=\"Example,\"\nCustomer" });
        var file = (await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "review")).Value!;
        using var parser = new TextFieldParser(new StringReader(file.Csv)) { Delimiters = [","], HasFieldsEnclosedInQuotes = true };
        parser.ReadFields(); var fields = parser.ReadFields()!;
        Assert.Equal("'=\"Example,\"\nCustomer", fields[4]); Assert.Equal("'+971500003001", fields[5]);
        Assert.Equal("500.00", fields[10]); Assert.Equal(22, fields.Length); Assert.Equal(new[] { "false", "false", "false" }, fields[19..]);
    }
}

using System.Reflection;
using Microsoft.VisualBasic.FileIO;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;

namespace TigerCS.Tests.Collections.Services;

/// <summary>
/// Verification of the approved campaign policy (schedule table 7 Oct 2026, per-unit thresholds 8 Oct 2026) around the things the
/// existing tests do not pin: the configured <c>StartDate</c> as the one lower bound, the 2026 and 2027 preview years, per-unit
/// eligibility and de-duplication, settled/credit rows, and the CSV contract against its documentation.
/// What these tests do <b>not</b> prove is listed in <c>docs/system/10-collections-and-campaigns.md</c>
/// ("Verification of campaign behaviour").
/// </summary>
public sealed class CollectionsCampaignVerificationTests
{
    private static DateTime D(int y, int m, int d) => new(y, m, d);

    private static PactReceivableInstalment R(DateTime due, decimal amount, string tenant, int unit = 101, int company = 4) =>
        CollectionsCampaignAppServiceTests.Row(10, amount, tenant, company, unit) with { DueDate = due };

    private static async Task<List<string>> Tenants(CollectionsCampaignAppServiceTests.Harness h, string stage, DateOnly date)
    {
        var result = await h.Service.PreviewAsync(h.Manager, stage, date);
        Assert.True(result.IsSuccess, result.Detail);
        return result.Value!.Items.Select(c => c.TenantId).Order().ToList();
    }

    // =====================================================================
    //  StartDate is the one lower bound
    // =====================================================================

    [Fact]
    public async Task TheConfiguredStartDate_IsTheLowerBound_ForPreview_ReviewCsv_AndGenesysCsv()
    {
        var h = new CollectionsCampaignAppServiceTests.Harness();
        h.Sql.StartDate = new DateTime(2026, 3, 1); // not the committed 2026-01-01: proves the option itself is what is read
        h.Campaign.FinancialSourceValidated = true;
        h.Source.Items.AddRange([R(D(2026, 2, 5), 900m, "BEFORE"), R(D(2026, 3, 5), 900m, "AFTER"), R(D(2026, 10, 10), 500m, "CURRENT", unit: 202)]);

        var overdue = (await h.Service.PreviewAsync(h.Manager, "OverdueReminder", new(2026, 10, 8))).Value!;
        Assert.Equal("AFTER", Assert.Single(overdue.Items).TenantId);
        Assert.Equal(new DateOnly(2026, 3, 1), overdue.DateFrom);
        Assert.Equal(new DateOnly(2026, 3, 1), h.Source.LastRequest!.FromDate);

        var review = (await h.Service.ExportAsync(h.Manager, "OverdueReminder", "review", new(2026, 10, 8))).Value!;
        Assert.DoesNotContain("BEFORE", review.Csv);
        Assert.Contains("AFTER", review.Csv);
        Assert.Equal(new DateOnly(2026, 3, 1), h.Source.LastRequest!.FromDate);

        // Today in the test clock is 14 Oct 2026 = the CurrentMonthReminder day.
        var genesys = await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "genesys");
        Assert.True(genesys.IsSuccess, genesys.Detail);
        Assert.Equal(new DateOnly(2026, 3, 1), h.Source.LastRequest!.FromDate);
    }

    [Fact]
    public async Task TheReceivablesList_AsksTheSourceForItsConfiguredStart_NotAnOwnDate()
    {
        var h = new CollectionsCampaignAppServiceTests.Harness();
        var list = new PactReceivableCustomersAppService(h.Options, h.Sql, new(h.Options, new TigerCS.Tests.IdentityAndAccess.Fakes.FakeDepartmentRepository()),
            new(h.Options, new TigerCS.Tests.Notifications.Fakes.FakeTimeProvider(new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc))),
            h.Source, Microsoft.Extensions.Logging.Abstractions.NullLogger<PactReceivableCustomersAppService>.Instance);

        var result = await list.ListAsync(h.Manager);

        Assert.True(result.IsSuccess, result.Detail);
        // FromDate null = "the source applies PactReceivablesOptions.StartDate" (PactSqlReceivablesSource: request.FromDate ?? options.StartDate).
        Assert.Null(h.Source.LastRequest!.FromDate);
    }

    [Fact]
    public void NoCollectionsCodeHardCodesTheStartDate_OutsideTheOption()
    {
        var root = Path.Combine(ApiRouteRoot(), "src");
        var files = new[] { "TigerCS.Application/Modules/Collections", "TigerCS.Infrastructure/Modules/Collections", "TigerCS.Web/Pages/Collections", "TigerCS.Api/Controllers" }
            .SelectMany(d => Directory.GetFiles(Path.Combine(root, d), "*.cs*", System.IO.SearchOption.AllDirectories))
            .Where(f => !f.EndsWith("PactReceivablesOptions.cs", StringComparison.Ordinal))
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".cshtml", StringComparison.Ordinal));

        var offenders = files.Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"2026, ?1, ?1\)|""2026-01-01""|1 Jan of the preview year")).ToList();

        Assert.True(offenders.Count == 0, "A literal 2026-01-01 lower bound outside PactReceivablesOptions: " + string.Join(", ", offenders));
    }

    private static string ApiRouteRoot() => TigerCS.Tests.GenesysIntegration.Contracts.ApiRouteCatalog.RepoRoot();

    [Fact]
    public async Task AWindowEndingBeforeTheStartDate_IsRefused_NotWidenedToACalendarYear()
    {
        var h = new CollectionsCampaignAppServiceTests.Harness();
        h.Source.Items.Add(R(D(2025, 12, 20), 30000m, "OLD"));

        var result = await h.Service.PreviewAsync(h.Manager, "OverdueReminder", new(2025, 12, 31));

        Assert.Equal(CollectionsOutcome.InvalidRequest, result.Outcome);
        Assert.Contains("2026-01-01", result.Detail);
        Assert.Equal(0, h.Source.Reads);
    }

    [Fact]
    public async Task AnExplicitFromDate_StillOverridesTheStartDate_AsDocumented()
    {
        var h = new CollectionsCampaignAppServiceTests.Harness();
        h.Source.Items.AddRange([R(D(2025, 11, 20), 900m, "OLD"), R(D(2026, 3, 5), 900m, "NEW")]);

        var report = (await h.Service.PreviewAsync(h.Manager, "OverdueReminder", new(2026, 10, 8), dateFrom: new(2025, 1, 1))).Value!;

        Assert.Equal(["NEW", "OLD"], report.Items.Select(c => c.TenantId).Order());
        Assert.Equal(new PactReceivablesRequest(new(2025, 1, 1), new(2026, 10, 8), null), h.Source.LastRequest);
    }

    // =====================================================================
    //  Preview years 2026 and 2027
    // =====================================================================

    [Fact]
    public async Task Preview_8Oct2026_RetainsOlderArrearsAndLegalAccounts_AndExcludesRowsBeforeStartDate()
    {
        var h = new CollectionsCampaignAppServiceTests.Harness();
        h.Source.Items.AddRange([
            R(D(2025, 12, 20), 30000m, "T1-PRESTART", 101),   // before 2026-01-01: out of scope for every stage
            R(D(2026, 2, 5), 21000m, "T2-OLD-LEGAL", 102),    // overdue > 1 month AND older than 3 months, > AED 20,000
            R(D(2026, 9, 10), 2000m, "T3-LASTMONTH", 103),    // previous month, > AED 1,500
            R(D(2026, 8, 15), 800m, "T4-AUG", 104)            // overdue > 1 month, below the referral threshold
        ]);

        Assert.Equal(["T2-OLD-LEGAL", "T4-AUG"], await Tenants(h, "OverdueReminder", new(2026, 10, 8)));
        Assert.Equal(["T3-LASTMONTH"], await Tenants(h, "LegalNotice", new(2026, 10, 8)));
        Assert.Equal(["T2-OLD-LEGAL"], await Tenants(h, "LegalReferral", new(2026, 10, 8)));
        Assert.Equal(new DateOnly(2026, 1, 1), h.Source.LastRequest!.FromDate);
    }

    [Fact]
    public async Task Preview_1Feb2027_RetainsDec2026AndOct2026Arrears_AndTheOct2026Referral()
    {
        var h = new CollectionsCampaignAppServiceTests.Harness();
        h.Source.Items.AddRange([
            R(D(2025, 12, 31), 50000m, "D4-PRESTART", 104),
            R(D(2026, 12, 15), 900m, "D1-DEC2026", 101),
            R(D(2026, 10, 20), 25000m, "D2-OCT2026", 102),
            R(D(2027, 1, 10), 2500m, "D3-JAN2027", 103)
        ]);

        // The 2027 lookback: with a "1 January of the preview year" floor, D1 and D2 would have vanished from this list.
        Assert.Equal(["D1-DEC2026", "D2-OCT2026"], await Tenants(h, "OverdueReminder", new(2027, 2, 1)));
        Assert.Equal(["D2-OCT2026"], await Tenants(h, "LegalReferral", new(2027, 2, 1)));
        Assert.Equal(["D3-JAN2027"], await Tenants(h, "LegalNotice", new(2027, 2, 1)));
        Assert.Equal(new PactReceivablesRequest(new(2026, 1, 1), new(2027, 2, 1), null), h.Source.LastRequest);
    }

    [Fact]
    public async Task Preview_1Jan2027_KeepsDecemberForTheLegalNotice_AndOctoberForTheOverdueReminder()
    {
        var h = new CollectionsCampaignAppServiceTests.Harness();
        h.Source.Items.AddRange([
            R(D(2026, 12, 5), 3000m, "E1-DEC", 101),        // previous month: legal notice, not yet "overdue by a month"
            R(D(2026, 10, 10), 1000m, "E2-OCT", 102),       // overdue by more than a month, not older than 3 months
            R(D(2026, 8, 30), 20001m, "E3-AUG", 103)        // overdue and older than 3 months, strictly above AED 20,000
        ]);

        Assert.Equal(["E2-OCT", "E3-AUG"], await Tenants(h, "OverdueReminder", new(2027, 1, 1)));
        Assert.Equal(["E1-DEC"], await Tenants(h, "LegalNotice", new(2027, 1, 1)));
        Assert.Equal(["E3-AUG"], await Tenants(h, "LegalReferral", new(2027, 1, 1)));
    }

    // =====================================================================
    //  Eligibility, de-duplication, settled accounts
    // =====================================================================

    [Fact]
    public async Task OneUnitIsOneContactPerStage_WithTheSumAndTheEarliestDueDate()
    {
        var h = new CollectionsCampaignAppServiceTests.Harness();
        h.Source.Items.AddRange([R(D(2026, 10, 3), 300m, "3001"), R(D(2026, 10, 20), 200m, "3001")]);

        var contact = Assert.Single((await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder")).Value!.Items);

        Assert.Equal(500m, contact.Amount);
        Assert.Equal(new DateOnly(2026, 10, 3), contact.DueDate);
    }

    [Fact]
    public async Task ThresholdsArePerUnit_AndTwoQualifyingUnitsOfOneCustomerAreTwoContacts()
    {
        var h = new CollectionsCampaignAppServiceTests.Harness();
        // Customer 3001: two units, AED 1,000 each in September - AED 2,000 in total, but 1,000 per unit is not above 1,500.
        h.Source.Items.AddRange([R(D(2026, 9, 5), 1000m, "3001", 101), R(D(2026, 9, 6), 1000m, "3001", 102)]);
        Assert.Empty((await h.Service.PreviewAsync(h.Manager, "LegalNotice", new(2026, 10, 8))).Value!.Items);

        h.Source.Items.Clear();
        h.Source.Items.AddRange([R(D(2026, 9, 5), 1600m, "3001", 101), R(D(2026, 9, 6), 1000m, "3001", 102)]);
        var one = Assert.Single((await h.Service.PreviewAsync(h.Manager, "LegalNotice", new(2026, 10, 8))).Value!.Items);
        Assert.Equal("TP140-101", one.UnitCode);

        h.Source.Items.Clear();
        h.Source.Items.AddRange([R(D(2026, 9, 5), 1600m, "3001", 101), R(D(2026, 9, 6), 1700m, "3001", 102)]);
        var two = (await h.Service.PreviewAsync(h.Manager, "LegalNotice", new(2026, 10, 8))).Value!.Items;
        Assert.Equal(2, two.Count);
        Assert.Equal(2, two.Select(c => c.RecordId).Distinct().Count());
        Assert.All(two, c => Assert.Equal("ext:Pact:3001", c.CustomerKey));
    }

    [Fact]
    public async Task SettledRows_CreditsAndPartPayments_AreHandledAsDocumented()
    {
        var h = new CollectionsCampaignAppServiceTests.Harness();
        h.Campaign.FinancialSourceValidated = true;
        h.Source.Items.AddRange([
            R(D(2026, 10, 3), 0m, "PAID", 101) with { SourceStatus = "Paid" },       // settled: removed before eligibility
            R(D(2026, 10, 4), 120m, "PART", 102),                                  // part-paid: only the remaining 120 counts
            R(D(2026, 10, 5), -500m, "CREDITONLY", 103),                           // a credit is never a reminder
            R(D(2026, 10, 6), 300m, "CREDITPLUS", 104),                            // arrears plus a credit on another date:
            R(D(2026, 10, 7), -500m, "CREDITPLUS", 104),                           //   the credit does NOT net the arrears
            R(D(2026, 10, 8), 400m, "LABELLEDPAID", 105) with { SourceStatus = "Paid" } // contradictory: positive balance labelled Paid
        ]);

        var items = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder")).Value!.Items.ToDictionary(c => c.TenantId);

        Assert.DoesNotContain("PAID", items.Keys);
        Assert.DoesNotContain("CREDITONLY", items.Keys);
        Assert.Equal(120m, items["PART"].Amount);
        Assert.Equal(300m, items["CREDITPLUS"].Amount);
        Assert.Contains("ContradictoryPaymentStatus", items["LABELLEDPAID"].Reason);
        Assert.Equal("NeedsReview", items["LABELLEDPAID"].Status);
    }

    [Fact]
    public async Task MissingIdentityAndContacts_NeverReachAGenesysFile()
    {
        var h = new CollectionsCampaignAppServiceTests.Harness();
        h.Campaign.FinancialSourceValidated = true;
        h.Source.Items.Add(CollectionsCampaignAppServiceTests.Row() with { UnitId = null });
        Assert.Contains("MissingUnitIdentity", Assert.Single((await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder")).Value!.Items).Reason);
        Assert.False((await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "genesys")).IsSuccess);

        h.Source.Items.Clear();
        h.Source.Items.Add(CollectionsCampaignAppServiceTests.Row() with { Mobile = "", Email = "not-an-email" });
        var noContact = Assert.Single((await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder")).Value!.Items);
        Assert.Contains("NoValidContact", noContact.Reason);
        Assert.False((await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "genesys")).IsSuccess);
    }

    [Fact]
    public async Task OnDay14_BothCurrentMonthAndLegalNoticeQualify_NoPriorityIsApplied()
    {
        var h = new CollectionsCampaignAppServiceTests.Harness();
        h.Source.Items.AddRange([R(D(2026, 9, 20), 2000m, "3001", 101), R(D(2026, 10, 10), 500m, "3001", 102)]);

        var current = await Tenants(h, "CurrentMonthReminder", new(2026, 10, 14));
        var legal = await Tenants(h, "LegalNotice", new(2026, 10, 14));

        Assert.Equal(["3001"], current);
        Assert.Equal(["3001"], legal);
    }

    [Fact]
    public async Task AllocationAmbiguity_IsJudgedOverTheWholeReadWindow_NotOnlyTheStagesQualifyingRows()
    {
        // The default lookback is the whole StartDate..To window, so a same-date pair under two units in March flags the tenant's
        // October contact too. Stricter than necessary, and pinned so a change is a decision (Collections to confirm).
        var h = new CollectionsCampaignAppServiceTests.Harness();
        h.Source.Items.AddRange([R(D(2026, 3, 5), 100m, "3001", 101), R(D(2026, 3, 5), 100m, "3001", 102), R(D(2026, 10, 10), 500m, "3001", 101)]);

        var wide = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder")).Value!.Items;
        Assert.Contains("UnitAllocationNeedsReview", Assert.Single(wide, c => c.UnitCode == "TP140-101").Reason);

        var narrow = (await h.Service.PreviewAsync(h.Manager, "CurrentMonthReminder", dateFrom: new(2026, 10, 1))).Value!.Items;
        Assert.DoesNotContain("UnitAllocationNeedsReview", Assert.Single(narrow).Reason);
    }

    [Fact]
    public void ThePreviewAndExport_HaveNoRepositoryOutboxOrAuditDependency_SoTheyRecordNothing()
    {
        var parameters = typeof(CollectionsCampaignAppService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType.Name).ToList();

        Assert.DoesNotContain(parameters, n => n.Contains("Repository") || n.Contains("UnitOfWork") || n.Contains("Outbox") || n.Contains("Audit"));
    }

    // =====================================================================
    //  CSV contract
    // =====================================================================

    [Fact]
    public async Task TheCsvColumns_AreExactlyTheDocumentedOnes_InTheDocumentedOrder()
    {
        var doc = File.ReadAllText(Path.Combine(ApiRouteRoot(), "docs", "Collections", "Collections-Campaigns.md"));
        var table = doc[doc.IndexOf("| CSV columns | Meaning |", StringComparison.Ordinal)..];
        var documented = table.Split('\n').Skip(2).TakeWhile(l => l.StartsWith('|'))
            .SelectMany(l => l.Split('|')[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Select(c => c.Trim('`')).ToList();

        var h = new CollectionsCampaignAppServiceTests.Harness();
        h.Source.Items.Add(CollectionsCampaignAppServiceTests.Row());
        var csv = (await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "review")).Value!.Csv;
        var header = csv.Split("\r\n")[0].Split(',').ToList();

        Assert.Equal(documented, header);
        Assert.Equal(22, header.Count);
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1+1")]
    [InlineData("-1+1")]
    [InlineData("@SUM(A1)")]
    [InlineData("\t=1+1")]
    [InlineData("  =HYPERLINK(\"http://x\")")]
    public async Task EveryCsvTextCell_BeginningWithAFormulaTrigger_IsNeutralised_InBothModes(string name)
    {
        var h = new CollectionsCampaignAppServiceTests.Harness();
        h.Campaign.FinancialSourceValidated = true;
        h.Source.Items.Add(CollectionsCampaignAppServiceTests.Row() with { FullName = name, ProjectCode = name });

        foreach (var mode in new[] { "review", "genesys" })
        {
            var csv = (await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", mode)).Value;
            Assert.True(csv is not null, $"{mode} export failed");
            var fields = Parse(csv.Csv);
            Assert.StartsWith("'", fields[4]);   // CustomerName
            Assert.StartsWith("'", fields[9]);   // ProjectCode
            Assert.Equal(name, fields[4][1..]);  // nothing else is changed
        }
    }

    [Fact]
    public async Task ArabicNames_AreKeptExactly_AndAGenesysFileKeepsTheE164Phone_WhileAReviewFileNeutralisesIt()
    {
        var h = new CollectionsCampaignAppServiceTests.Harness();
        h.Campaign.FinancialSourceValidated = true;
        h.Source.Items.Add(CollectionsCampaignAppServiceTests.Row() with { FullName = "محمد أحمد \"الكبير\", المالك" });

        var genesys = Parse((await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "genesys")).Value!.Csv);
        var review = Parse((await h.Service.ExportAsync(h.Manager, "CurrentMonthReminder", "review")).Value!.Csv);

        Assert.Equal("محمد أحمد \"الكبير\", المالك", genesys[4]);
        Assert.Equal("محمد أحمد \"الكبير\", المالك", review[4]);
        Assert.Equal("+971500003001", genesys[5]);
        Assert.Equal("'+971500003001", review[5]);
        Assert.Equal("GenesysCampaign", genesys[18]);
        Assert.Equal("InternalReviewOnly", review[18]);
    }

    private static string[] Parse(string csv)
    {
        using var parser = new TextFieldParser(new StringReader(csv)) { Delimiters = [","], HasFieldsEnclosedInQuotes = true };
        parser.ReadFields();
        return parser.ReadFields()!;
    }
}

using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Modules.Collections;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Rcv;

/// <summary>
/// V012 + R004 against a REAL SQL Server: the company of a CRM owner is decided by the exact unit (never the tower number alone), tower names come from one source, the CRM contact reaches
/// Receivables / Campaigns / Payment, and everything is listed newest tower first with a total order that pages can be cut from. Skipped (never "passed") without TIGERCS_PERF_SQL.
/// </summary>
public sealed partial class RcvRealSqlTests
{
    private static readonly CollectionsCaller Manager2 = new(Guid.NewGuid(), [Roles.CsManager], []);

    private static SqlCollectionsCrmOwnerStore Crm() => new(Config(), new ReceivablesSnapshotOptions());

    private static CrmOwnerRow Owner(string key, int customer, string name, string mobile, string email, string projectName = "", int project = 7, int lead = 0, string? projectCode = null)
    {
        var dash = key.IndexOf('-');
        return new(key, projectCode ?? key[..dash], key[(dash + 1)..], lead == 0 ? customer * 10 : lead, 8, customer, 5000 + customer, project, name, mobile, email,
            CollectionsContactNormalizer.NormalizePhone(mobile), CollectionsContactNormalizer.NormalizeEmail(email), projectName);
    }

    private static async Task ResetCrm() =>
        await Exec("DELETE FROM dbo.CollectionsCrmUnitOwner; UPDATE dbo.CollectionsCrmOwnerState SET CurrentRunId = NULL, LastSuccessUtc = NULL, RowCount_ = 0, UnlinkedRows = 0, AmbiguousRows = 0, " +
                   "LastNote = NULL, LastAttemptStatus = NULL, LastError = NULL WHERE Id = 1; DELETE FROM dbo.CollectionsTowers WHERE NOT ((CompanyId = 4 AND TowerNumber BETWEEN 101 AND 130 AND TowerNumber <> 119) OR (CompanyId = 32 AND TowerNumber IN (127, 140))); DELETE FROM dbo.CollectionsCrmProjectMap WHERE Note = 'rcv-test';");

    private static CollectionsUnitLinkService LinkService() =>
        new(Crm(), new CollectionsCrmOwnersOptions { Enabled = true, MaxAgeMinutes = 1_000_000 }, TimeProvider.System, NullLogger<CollectionsUnitLinkService>.Instance);

    private static CollectionsCampaignAppService CampaignServiceWithCrm(RcvReceivablesSource source)
    {
        var options = new CollectionsOptions { Enabled = true };
        return new(options, new CollectionsCampaignOptions { FinancialSourceValidated = true }, new PactReceivablesOptions { Enabled = true, DefaultMinOutstandingAmount = 0m },
            new(options, new FakeDepartmentRepository()), new(options, new FakeTimeProvider(new DateTime(2026, 10, 11, 5, 0, 0, DateTimeKind.Utc))), source, NullLogger<CollectionsCampaignAppService>.Instance, LinkService());
    }

    private static async Task<List<string?[]>> Rows(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString); await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection); await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string?[]>();
        while (await reader.ReadAsync()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).ToArray());
        return rows;
    }

    private static string ScriptRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "database", "collections-receivables"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "database", "collections-receivables");
    }

    /// <summary>Runs a script file (batches split on GO) and returns the rows of every result set of every batch.</summary>
    private static async Task<List<List<string?[]>>> RunScript(string path, Func<string, string>? edit = null)
    {
        var text = File.ReadAllText(path);
        if (edit is not null) text = edit(text);
        var sets = new List<List<string?[]>>();
        await using var connection = new SqlConnection(ConnectionString); await connection.OpenAsync();
        foreach (var batch in Regex.Split(text, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            await using var command = new SqlCommand(batch, connection) { CommandTimeout = 300 };
            await using var reader = await command.ExecuteReaderAsync();
            do
            {
                var rows = new List<string?[]>();
                while (await reader.ReadAsync()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).ToArray());
                if (reader.FieldCount > 0) sets.Add(rows);
            } while (await reader.NextResultAsync());
        }
        return sets;
    }

    // ---- the company of a CRM owner is decided by the exact unit ----

    [SqlFact]
    public async Task ATowerInBothCompanies_IsTiedToTheCompanyThatHoldsTheExactUnit_AndTheReportedCrmContactReachesEveryPage()
    {
        await Reset(); await ResetCrm();
        try
        {
            // Tower 127 is in dbo.CollectionsTowers under BOTH companies (Dubai "Tower 127", Sharjah "Faradis"): the tower number alone cannot name the company.
            await Pact(4, "TP127-1921", "D127", "Pact Name", "", "", 91921, "V1", "2026-10-05", 500);
            await Pact(32, "TP127-2000", "S127", "Sharjah Pact", "+971500000099", "", 92000, "V2", "2026-10-05", 300);
            Assert.Equal("Succeeded", (await Refresher().RefreshAsync("Test", null, default)).Status);
            var linked = await Crm().ReplaceAsync([
                Owner("TP127-1921", 777, "Mohammad Mousa Abdul Hamid", "+966508650505", "mohammad.m.abdulhamid@gmail.com", "Blue Wave", lead: 256146),
                Owner("TP127-2000", 778, "Sharjah Buyer", "+971500000050", "")], default, "test note");
            Assert.Equal(2, linked);
            Assert.Equal(["4/Snapshot", "32/Snapshot"], (await Rows("SELECT CONCAT(CompanyId, '/', CompanySource) FROM dbo.CollectionsCrmUnitOwner ORDER BY LeadId DESC, OwnerSeq")).Select(r => r[0]!));
            Assert.Equal("test note", await Scalar<string>("SELECT LastNote FROM dbo.CollectionsCrmOwnerState WHERE Id = 1"));

            // Receivables
            var page = await Page(minTotal: null);
            var unit = page.Units.Single(u => u.UnitCode == "TP127-1921");
            Assert.Equal((1, "Mohammad Mousa Abdul Hamid", "+966508650505", "mohammad.m.abdulhamid@gmail.com", false), (unit.CrmCustomers, unit.CrmName, unit.CrmPhone, unit.CrmEmail, unit.CrmCompanyAmbiguous));
            Assert.Equal(("Tower 127", 4), (unit.TowerName, unit.CompanyId));
            var sharjah = page.Units.Single(u => u.UnitCode == "TP127-2000");
            Assert.Equal(("Faradis", "+971500000050"), (sharjah.TowerName, sharjah.CrmPhone));
            Assert.DoesNotContain(page.Units.Where(u => u.CompanyId == 32), u => u.UnitCode == "TP127-1921");
            Assert.Null((await Page(minTotal: null, company: 32)).Units.SingleOrDefault(u => u.UnitCode == "TP127-1921"));

            // Campaigns
            var campaigns = (await CampaignServiceWithCrm(Source()).PreviewAsync(Manager2, "CurrentMonthReminder", dateFrom: new DateOnly(2000, 1, 1), minTotal: null)).Value!;
            var item = campaigns.Items.Single(i => i.UnitCode == "TP127-1921");
            Assert.Equal(("Mohammad Mousa Abdul Hamid", "+966508650505", "mohammad.m.abdulhamid@gmail.com", 777), (item.CustomerName, item.Phone, item.Email, item.CrmCustomerId));
            Assert.DoesNotContain("NoValidContact", item.Reason);
            Assert.Equal("Ready", campaigns.Crm!.State);

            // Payment (by unit): the link of company 4 only, never company 32's.
            var read = await Source().ReadUnitAsync("TP127-1921", null, Today, default);
            var link = Assert.Single(read.CrmLinks);
            Assert.Equal((4, CrmLinkStatus.Single, "+966508650505"), (link.Key, link.Value.Status, link.Value.Contact.Phone));
            Assert.True(read.CrmLoaded);
        }
        finally { await ResetCrm(); }
    }

    [SqlFact]
    public async Task ATowerMissingFromCollectionsTowers_StillLinksItsCrmOwners_AndTakesItsNameFromCrm()
    {
        await Reset(); await ResetCrm();
        try
        {
            Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM dbo.CollectionsTowers WHERE TowerNumber = 119"));   // TP119: not seeded (V001)
            await Pact(4, "TP119-501", "D119", "Pact Name", "", "", 11501, "V1", "2026-10-05", 500);
            Assert.Equal("Succeeded", (await Refresher().RefreshAsync("Test", null, default)).Status);
            Assert.Equal(1, await Crm().ReplaceAsync([Owner("TP119-501", 600, "Lagoon Buyer", "+966501112222", "lagoon@example.test", "Lagoon View")], default));
            Assert.Equal("4/Snapshot", (await Rows("SELECT CONCAT(CompanyId, '/', CompanySource) FROM dbo.CollectionsCrmUnitOwner")).Single()[0]);

            var unit = (await Page(minTotal: null)).Units.Single();
            Assert.Equal(("119", "Lagoon View", "+966501112222"), (unit.TowerNumber, unit.TowerName, unit.CrmPhone));
            var item = (await CampaignServiceWithCrm(Source()).PreviewAsync(Manager2, "CurrentMonthReminder", dateFrom: new DateOnly(2000, 1, 1), minTotal: null)).Value!.Items.Single();
            Assert.Equal(("119", "Lagoon View", "+966501112222"), (item.TowerNumber, item.TowerName, item.Phone));
            Assert.Equal("Lagoon View", (await Source().ReadUnitAsync("TP119-501", 4, Today, default)).Identities.Single().TowerName);
            // The picker lists rows of CollectionsTowers: without a row the tower cannot be chosen yet (tools/repair_tower_names.sql adds it from the same CRM name).
            Assert.DoesNotContain(await Rows("EXEC dbo.usp_CollectionsRcv_GetTowers"), t => t[1] == "119");
        }
        finally { await ResetCrm(); }
    }

    [SqlFact]
    public async Task AUnitBothCompaniesHold_IsTiedToNeither_IsFlagged_AndIsRecomputedWhenTheSnapshotChanges()
    {
        await Reset(); await ResetCrm();
        try
        {
            await Pact(4, "TP127-700", "D700", "Dubai Pact", "+971500003001", "", 7001, "V1", "2026-10-05", 500);
            await Pact(32, "TP127-700", "S700", "Sharjah Pact", "+971500003002", "", 7002, "V2", "2026-10-05", 300);
            Assert.Equal("Succeeded", (await Refresher().RefreshAsync("Test", null, default)).Status);
            Assert.Equal(0, await Crm().ReplaceAsync([Owner("TP127-700", 700, "Some Buyer", "+966501110000", "")], default));      // tied to nobody

            Assert.Equal(("Ambiguous", 1, 1), ((await Rows("SELECT CompanySource FROM dbo.CollectionsCrmUnitOwner")).Single()[0]!, await Scalar<int>("SELECT UnlinkedRows FROM dbo.CollectionsCrmOwnerState WHERE Id = 1"), await Scalar<int>("SELECT AmbiguousRows FROM dbo.CollectionsCrmOwnerState WHERE Id = 1")));
            var page = await Page(minTotal: null);
            Assert.Equal(2, page.Units.Count);
            Assert.All(page.Units, u => Assert.Equal((true, null, 1), (u.CrmCompanyAmbiguous, u.CrmPhone, u.CrmCustomers)));      // flagged for BOTH companies, no CRM contact used for either
            var campaigns = (await CampaignServiceWithCrm(Source()).PreviewAsync(Manager2, "CurrentMonthReminder", dateFrom: new DateOnly(2000, 1, 1), minTotal: null)).Value!;
            Assert.All(campaigns.Items, i => { Assert.Contains("CrmCompanyAmbiguous", i.Reason); Assert.Equal("NeedsReview", i.Status); Assert.Null(i.CrmCustomerId); });
            Assert.Equal(["+971500003001", "+971500003002"], campaigns.Items.Select(i => i.Phone).Order());   // each keeps its own PACT contact

            // Sharjah's account of the unit is settled and the snapshot is refreshed: only Dubai holds it now - the refresh re-ties the owner (the application calls the resolver after each refresh).
            await Exec("DELETE FROM dbo.FakeEdgeRows WHERE CompanyId = 32", "PACTRPT");
            await Pact(32, "TP127-701", "S701", "Other Sharjah", "+971500003003", "", 7003, "V3", "2026-10-06", 100);
            Assert.Equal("Succeeded", (await Refresher().RefreshAsync("Test", null, default)).Status);
            Assert.Equal("4/Snapshot", (await Rows("SELECT CONCAT(CompanyId, '/', CompanySource) FROM dbo.CollectionsCrmUnitOwner")).Single()[0]);
            var resolved = (await Page(minTotal: null)).Units.Single(u => u.UnitCode == "TP127-700");
            Assert.Equal((false, "+966501110000", 4), (resolved.CrmCompanyAmbiguous, resolved.CrmPhone, resolved.CompanyId));

            // ... and ambiguous again when Sharjah holds it again.
            await Pact(32, "TP127-700", "S700", "Sharjah Pact", "+971500003002", "", 7002, "V2", "2026-10-05", 300);
            Assert.Equal("Succeeded", (await Refresher().RefreshAsync("Test", null, default)).Status);
            Assert.Equal("Ambiguous", (await Rows("SELECT CompanySource FROM dbo.CollectionsCrmUnitOwner")).Single()[0]);
        }
        finally { await ResetCrm(); }
    }

    [SqlFact]
    public async Task AVerifiedProjectMap_WinsOverTheFeedsCode_AndMayNameTheCompany()
    {
        await Reset(); await ResetCrm();
        try
        {
            await Pact(32, "TP127-910", "S910", "Sharjah Pact", "", "", 9101, "V1", "2026-10-05", 400);
            Assert.Equal("Succeeded", (await Refresher().RefreshAsync("Test", null, default)).Status);
            await Exec("INSERT dbo.CollectionsCrmProjectMap (CrmProjectId, ProjectCode, CompanyId, CrmProjectName, VerifiedBy, Note) VALUES (4242, 'TP127', 32, 'Faradis', 'test', 'rcv-test')");
            // CRM sent a different code for the project (BW127): the verified map rebuilds the unit key from TP127 and names the company.
            Assert.Equal(1, await Crm().ReplaceAsync([Owner("TPBW127-910", 910, "Mapped Buyer", "+966501234000", "", "Faradis", project: 4242, projectCode: "BW127")], default));
            Assert.Equal(("TP127-910", "TP127", 32, "Map"), ParseOwner((await Rows("SELECT UnitKey, ProjectCode, CompanyId, CompanySource FROM dbo.CollectionsCrmUnitOwner")).Single()));
            Assert.Equal("+966501234000", (await Page(minTotal: null)).Units.Single().CrmPhone);
        }
        finally { await ResetCrm(); }
    }

    private static (string, string, int, string) ParseOwner(string?[] r) => (r[0]!, r[1]!, int.Parse(r[2]!), r[3]!);

    // ---- tower names: one source ----

    [SqlFact]
    public async Task TowerNames_AreCollectionsTowersFirst_ThenTheSingleCrmName_NeverAPlaceholderOrAnAmbiguousName()
    {
        await Reset(); await ResetCrm();
        try
        {
            await Exec("INSERT dbo.CollectionsTowers (TowerNumber, TowerName, CompanyId) VALUES (150, N'150', 4), (151, N'Real Name', 4), (152, N'TP152', 4), (153, N'', 4), (154, N'Tower 154', 4)");
            Assert.Equal(6, await Crm().ReplaceAsync([
                Owner("TP150-1", 1, "A", "+971500000150", "", "Sea Breeze"),                         // placeholder name in CollectionsTowers -> CRM name
                Owner("TP151-1", 2, "B", "+971500000151", "", "Other Name"),                         // real name in CollectionsTowers wins
                Owner("TP152-1", 3, "C", "+971500000152", "", "Sea Wind"), Owner("TP152-2", 4, "D", "+971500000153", "", "Sea Wave"),   // two different CRM names -> no name
                Owner("TP153-1", 5, "E", "+971500000154", "", "TP153"),                              // CRM "name" that is only the number -> no name
                Owner("TP154-1", 6, "F", "+971500000155", "", "Another")], default));
            var names = (await Rows("SELECT CompanyId, TowerNumber, TowerName, NameSource FROM dbo.fn_Collections_TowerNames() WHERE TRY_CONVERT(int, TowerNumber) >= 150 ORDER BY TRY_CONVERT(int, TowerNumber)"))
                .ToDictionary(r => r[1]!, r => (r[2]!, r[3]!));
            Assert.Equal(("Sea Breeze", "Crm"), names["150"]);
            Assert.Equal(("Real Name", "CollectionsTowers"), names["151"]);
            Assert.False(names.ContainsKey("152"));
            Assert.False(names.ContainsKey("153"));
            Assert.Equal(("Tower 154", "CollectionsTowers"), names["154"]);                         // a real stored name "Tower 154" is a name
        }
        finally { await ResetCrm(); }
    }

    [SqlFact]
    public async Task TheSqlNamePredicate_AgreesWithTheApplicationOne()
    {
        await Reset(); await ResetCrm();
        try
        {
            var samples = new[] { "Blue Wave", "Tower 160", "160", " 160 ", "TP160", "tp-160", "", "   ", "Faradis", "160 North" };
            for (var i = 0; i < samples.Length; i++)
                await Exec($"INSERT dbo.CollectionsTowers (TowerNumber, TowerName, CompanyId) VALUES ({160 + i}, N'{samples[i]}', 4)");
            var inFunction = (await Rows("SELECT TowerNumber FROM dbo.fn_Collections_TowerNames() WHERE TRY_CONVERT(int, TowerNumber) >= 160")).Select(r => int.Parse(r[0]!)).ToHashSet();
            for (var i = 0; i < samples.Length; i++)
                Assert.Equal(CollectionsTowerName.IsUsable(samples[i], (160 + i).ToString()), inFunction.Contains(160 + i));
        }
        finally { await ResetCrm(); }
    }

    // ---- newest tower first ----

    [SqlFact]
    public async Task TheTowerPicker_IsOrderedByTheNumberDescending_NotByTextOrName()
    {
        await Reset(); await ResetCrm();
        try
        {
            await Exec("INSERT dbo.CollectionsTowers (TowerNumber, TowerName, CompanyId) VALUES (99, N'Zulu', 4), (1000, N'Alpha', 4), (152, N'Mid', 32)");
            var towers = (await Rows("EXEC dbo.usp_CollectionsRcv_GetTowers")).Select(r => (Number: int.Parse(r[1]!), Company: int.Parse(r[3]!), Id: long.Parse(r[0]!))).ToList();
            var expected = towers.ToList();
            expected.Sort((a, b) => CollectionsTowerOrder.CompareTowers(a.Number.ToString(), a.Company, a.Id, b.Number.ToString(), b.Company, b.Id));
            Assert.Equal(expected, towers);                                                         // the procedure's order IS the application's order
            Assert.Equal(1000, towers[0].Number);                                                   // numeric, so 1000 > 152 > 140 ... > 99 > 1
            Assert.True(towers.FindIndex(t => t.Number == 99) > towers.FindIndex(t => t.Number == 101));   // as text "99" would sit among the 9x
            Assert.True(towers.Select(t => t.Number).Zip(towers.Select(t => t.Number).Skip(1)).All(p => p.First >= p.Second));
            var catalog = new SqlCollectionsTowerCatalog(Config(), new ReceivablesSnapshotOptions(), NullLogger<SqlCollectionsTowerCatalog>.Instance);
            Assert.Equal(towers.Select(t => t.Id), (await catalog.ListActiveAsync(default)).Select(t => (long)t.TowerId));
        }
        finally { await ResetCrm(); }
    }

    private static async Task SeedManyTowers()
    {
        await Reset();
        var n = 0;
        // 14 towers (119..132 plus a non-numeric one), a few units each, in both companies; tenants and unit ids are unique per unit.
        foreach (var tower in new[] { "119", "120", "121", "122", "123", "124", "125", "126", "127", "128", "129", "130", "131", "132", "9A" })
            foreach (var unit in new[] { "1000", "99", "C-402", "302", "1921" })
            {
                n++;
                var company = n % 3 == 0 ? 32 : 4;
                await Pact(company, $"TP{tower}-{unit}", $"X{n:D3}", $"Customer {n}", $"+9715000{n:D5}", "", 20000 + n, $"V{n}", n % 2 == 0 ? "2026-10-05" : "2026-09-12", 100 + n);
            }
        Assert.Equal("Succeeded", (await Refresher().RefreshAsync("Test", null, default)).Status);
    }

    private static List<PactInstalmentUnitDto> Sorted(IEnumerable<PactInstalmentUnitDto> units)
    {
        var list = units.ToList();
        list.Sort((a, b) => CollectionsTowerOrder.Compare(CollectionsTowerNumber.Parse(a.UnitCode), a.UnitCode, a.CompanyId, a.TenantId, a.UnitId, CollectionsTowerNumber.Parse(b.UnitCode), b.UnitCode, b.CompanyId, b.TenantId, b.UnitId));
        return list;
    }

    [SqlFact]
    public async Task Receivables_AreOrderedNewestTowerFirst_OverTheWholeResult_AndEveryPageIsACleanSliceOfThatOrder()
    {
        await SeedManyTowers();
        var everything = await Page(minTotal: null, pageSize: 100);
        Assert.Equal(75, everything.Units.Count);
        var expected = Sorted(everything.Units).Select(Key).ToList();
        Assert.Equal(expected, everything.Units.Select(Key));                                      // SQL order == the application's order (CollectionsTowerOrder)
        Assert.StartsWith("TP132-", everything.Units[0].UnitCode);                                  // TP132 first ...
        Assert.StartsWith("TP9A-", everything.Units[^1].UnitCode);                                  // ... the non-numeric tower after every numeric one
        Assert.Equal(["TP119-99", "TP119-302", "TP119-1000", "TP119-1921", "TP119-C-402"], everything.Units.Where(u => u.UnitCode.StartsWith("TP119-")).Select(u => u.UnitCode));   // inside a tower: unit number ascending, lettered last
        Assert.Equal(75, everything.Totals.UnitCount);

        foreach (var pageSize in new[] { 1, 7, 25, 74, 75 })
        {
            var seen = new List<string>();
            var pages = (75 + pageSize - 1) / pageSize;
            for (var page = 1; page <= pages; page++)
            {
                var read = await Page(minTotal: null, page: page, pageSize: pageSize);
                Assert.Equal(75, read.Totals.UnitCount);                                           // the count is of the whole filtered set, on every page
                seen.AddRange(read.Units.Select(Key));
            }
            Assert.Equal(expected, seen);                                                           // no unit repeated, none lost, in the one order
        }
        Assert.Empty((await Page(minTotal: null, page: 4, pageSize: 25)).Units);                  // past the end: nothing
    }

    [SqlFact]
    public async Task TheTowerOrder_HoldsWithEveryFilter_AndTheTotalsAreUnchanged()
    {
        await SeedManyTowers();
        var all = await Page(minTotal: null, pageSize: 100);
        foreach (var (status, minTotal, company, search) in new (string?, decimal?, int?, string?)[]
                 { ("due", null, null, null), ("overdue", null, null, null), (null, 200m, null, null), (null, null, 32, null), (null, null, 4, null), (null, null, null, "TP12"), ("overdue", 150m, 4, null) })
        {
            var filtered = await Page(minTotal: minTotal, status: status, company: company, search: search, pageSize: 100);
            Assert.Equal(Sorted(filtered.Units).Select(Key), filtered.Units.Select(Key));
            var order = all.Units.Select(Key).ToList();
            Assert.Equal(filtered.Units.Select(Key), order.Where(filtered.Units.Select(Key).ToHashSet().Contains));      // a filtered list is the same order, only shorter
            // paging a filtered list gives the same units as the unpaged one
            var paged = new List<string>();
            for (var page = 1; page <= (filtered.Units.Count + 5) / 6 + 1; page++) paged.AddRange((await Page(minTotal: minTotal, status: status, company: company, search: search, page: page, pageSize: 6)).Units.Select(Key));
            Assert.Equal(filtered.Units.Select(Key), paged);
            // Due / Overdue / Total are what the listed instalments add up to, whatever the order.
            Assert.Equal(filtered.Units.Sum(u => u.RemainingTotal), filtered.Totals.RemainingTotal);
            Assert.All(filtered.Units, u => Assert.Equal(u.RemainingTotal, u.Instalments.Sum(i => i.RemainingAmount)));
        }
        Assert.Equal(all.Totals.RemainingTotal, all.Totals.DueRemaining + all.Totals.OverdueRemaining);
    }

    [SqlFact]
    public async Task Campaigns_AndReceivables_ListTheSameUnitsInTheSameOrder()
    {
        await SeedManyTowers();
        var receivables = (await Page(minTotal: null, pageSize: 100)).Units.Select(u => $"{u.CompanyId}:{u.UnitCode}").ToList();
        var service = CampaignServiceWithCrm(Source());
        var pages = new List<string>();
        for (var page = 1; page <= 10; page++)
            pages.AddRange((await service.PreviewAsync(Manager2, "CurrentMonthReminder", dateFrom: new DateOnly(2000, 1, 1), minTotal: null, page: page, pageSize: 10)).Value!.Items.Select(i => $"{i.CompanyId}:{i.UnitCode}"));
        // Campaigns list the units that qualify for the stage (Receivables lists every Due + Overdue unit): the same units, in the same relative order.
        Assert.NotEmpty(pages);
        Assert.Equal(receivables.Where(pages.ToHashSet().Contains), pages);
        var csv = (await service.ExportAsync(Manager2, "CurrentMonthReminder", "review", dateFrom: new DateOnly(2000, 1, 1), minTotal: null)).Value!.Csv;
        Assert.Equal(pages.Select(r => r.Split(':')[1]), csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Split(',')[8].Trim('"')));
    }

    // ---- the re-runnable scripts ----

    [SqlFact]
    public async Task V012_IsIdempotent_AndLeavesTheCurrentRunLinked()
    {
        await Reset(); await ResetCrm();
        try
        {
            await Pact(4, "TP119-501", "D119", "Pact", "", "", 11501, "V1", "2026-10-05", 500);
            Assert.Equal("Succeeded", (await Refresher().RefreshAsync("Test", null, default)).Status);
            await Crm().ReplaceAsync([Owner("TP119-501", 600, "Lagoon Buyer", "+966501112222", "", "Lagoon View")], default);
            var before = await Rows("SELECT UnitKey, CompanyId, CompanySource, TowerNumber, ProjectName FROM dbo.CollectionsCrmUnitOwner");
            await RunScript(Path.Combine(ScriptRoot(), "V012__crm_owner_company_resolution_and_tower_names.sql"));
            await RunScript(Path.Combine(ScriptRoot(), "V012__crm_owner_company_resolution_and_tower_names.sql"));
            Assert.Equal(before.Select(r => string.Join('|', r)), (await Rows("SELECT UnitKey, CompanyId, CompanySource, TowerNumber, ProjectName FROM dbo.CollectionsCrmUnitOwner")).Select(r => string.Join('|', r)));
            Assert.Equal(["TP119-501|4|Snapshot|119|Lagoon View"], before.Select(r => string.Join('|', r)));
        }
        finally { await ResetCrm(); }
    }

    [SqlFact]
    public async Task RepairTowerNames_IsADryRunByDefault_AppliesOnlyWhenAsked_AndIsRepeatable()
    {
        await Reset(); await ResetCrm();
        try
        {
            await Exec("INSERT dbo.CollectionsTowers (TowerNumber, TowerName, CompanyId) VALUES (150, N'150', 4)");          // a row that only repeats its number
            await Pact(4, "TP119-501", "D119", "Pact", "", "", 11501, "V1", "2026-10-05", 500);                            // tower 119: no row at all
            await Pact(4, "TP150-1", "D150", "Pact", "", "", 11502, "V2", "2026-10-05", 500);
            await Pact(4, "TP155-1", "D155", "Pact", "", "", 11503, "V3", "2026-10-05", 500);                              // tower 155: nothing knows its name
            Assert.Equal("Succeeded", (await Refresher().RefreshAsync("Test", null, default)).Status);
            await Crm().ReplaceAsync([Owner("TP119-501", 600, "A", "+966501112222", "", "Lagoon View"), Owner("TP150-1", 601, "B", "+971500000150", "", "Sea Breeze")], default);
            var script = Path.Combine(ScriptRoot(), "tools", "repair_tower_names.sql");
            var towersBefore = await Rows("SELECT TowerNumber, TowerName, CompanyId FROM dbo.CollectionsTowers WHERE TowerNumber >= 119 AND TowerNumber NOT IN (127, 140) ORDER BY TowerNumber");

            // Dry run: proposes, writes nothing.
            var dry = await RunScript(script);
            Assert.Equal(["INSERT|4|119|Lagoon View", "RENAME|4|150|Sea Breeze"], dry[0].Select(r => $"{r[0]}|{r[1]}|{r[2]}|{r[5]}").OrderBy(x => x));
            Assert.Equal(["4|155"], dry[1].Select(r => $"{r[0]}|{r[1]}"));                                                  // reported, not written
            Assert.Equal(towersBefore, await Rows("SELECT TowerNumber, TowerName, CompanyId FROM dbo.CollectionsTowers WHERE TowerNumber >= 119 AND TowerNumber NOT IN (127, 140) ORDER BY TowerNumber"));

            // Apply.
            await RunScript(script, text => text.Replace("DECLARE @Apply bit = 0;", "DECLARE @Apply bit = 1;"));
            Assert.Equal(["119|Lagoon View|4", "150|Sea Breeze|4"], (await Rows("SELECT TowerNumber, TowerName, CompanyId FROM dbo.CollectionsTowers WHERE TowerNumber IN (119, 150) ORDER BY TowerNumber")).Select(r => string.Join('|', r)));
            Assert.Contains(await Rows("EXEC dbo.usp_CollectionsRcv_GetTowers"), t => t[1] == "119" && t[2] == "Lagoon View");   // now in the picker, with its name

            // Repeat: nothing left to do; a second apply changes nothing.
            var again = await RunScript(script, text => text.Replace("DECLARE @Apply bit = 0;", "DECLARE @Apply bit = 1;"));
            Assert.Empty(again[0]);
            Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM dbo.CollectionsTowers WHERE TowerNumber IN (119, 150)"));
        }
        finally { await ResetCrm(); }
    }

    [SqlFact]
    public async Task DiagnoseCrmUnit_NamesTheCauseForAUnit()
    {
        await Reset(); await ResetCrm();
        try
        {
            var script = Path.Combine(ScriptRoot(), "tools", "diagnose_crm_unit.sql");
            async Task<string> Verdict(string key) => (await RunScript(script, t => t.Replace("N'TP115-1921'", $"N'{key}'")))[^1].Single()[0]!;

            await Pact(4, "TP115-1921", "D115", "Pact", "", "", 11501, "V1", "2026-10-05", 500);
            await Pact(4, "TP127-800", "D800", "Pact", "", "", 11800, "V2", "2026-10-05", 500);
            await Pact(32, "TP127-800", "S800", "Pact", "", "", 11801, "V3", "2026-10-05", 500);
            Assert.Equal("Succeeded", (await Refresher().RefreshAsync("Test", null, default)).Status);
            Assert.Equal("CRM_FEED_NOT_LOADED", await Verdict("TP115-1921"));

            await Crm().ReplaceAsync([
                Owner("TP115-1921", 1, "Buyer", "+966508650505", "", "Blue Wave"), Owner("TP127-800", 2, "Buyer2", "+966508650506", "", ""),
                Owner("TP170-77", 3, "NoMobile", "", "", ""), Owner("TP170-78", 4, "BadMobile", "12345", "", "")], default);
            Assert.Equal("LINKED_OK", await Verdict("TP115-1921"));
            Assert.Equal("NO_OWNER_ROW", await Verdict("TP115-9999"));
            Assert.Equal("OWNER_COMPANY_AMBIGUOUS", await Verdict("TP127-800"));
            Assert.Equal("OWNER_WITHOUT_COMPANY", await Verdict("TP170-77"));                                                // no PACT account holds it and tower 170 is in no company
            await Exec("UPDATE dbo.CollectionsCrmUnitOwner SET CompanyId = 4, CompanySource = 'Tower' WHERE UnitKey IN ('TP170-77', 'TP170-78')");
            Assert.Equal("CRM_MOBILE_MISSING_IN_CRM", await Verdict("TP170-77"));
            Assert.Equal("CRM_MOBILE_NOT_A_VALID_NUMBER", await Verdict("TP170-78"));
        }
        finally { await ResetCrm(); }
    }
}

using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Infrastructure.Modules.Collections;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Collections.Rcv;

public sealed partial class RcvRealSqlTests
{
    private sealed record ParityRow(int CompanyId, string Tenant, string UnitCode, string Tower, string Customer, string Phone, string Email, decimal Due, decimal Overdue, decimal Total);

    /// <summary>The Receivables side exactly as the page builds a unit row (CRM first, PACT completing, the earliest payment's contact, Due / Overdue by the Dubai month).</summary>
    private static async Task<List<ParityRow>> ReceivablesRows(DateOnly from, DateOnly to, int? company, int? towerId, string? search, decimal? minTotal)
    {
        var phoneDigits = search is not null && search.All(c => char.IsDigit(c) || c == '+') ? new string(search.Where(char.IsDigit).ToArray()) : null;
        var page = await Source().ReadInstalmentUnitsAsync(new PactInstalmentsRequest(from, to, Today, 0m, "outstanding", search, phoneDigits, 1, 100, company, towerId, null, true, minTotal), default);
        var rows = new List<ParityRow>();
        foreach (var unit in page.Units)
        {
            var instalments = unit.Instalments.Where(i => i.RemainingAmount > 0 && CollectionsDueMonth.IsInScope(i.DueDate, Today)).ToList();
            if (instalments.Count == 0) continue;
            var contactRow = instalments.OrderBy(r => r.DueDate).ThenBy(r => r.VoucherNumber, StringComparer.Ordinal).First();
            var crm = unit.CrmCustomers is null ? CrmLinkStatus.None : unit.CrmCustomers > 1 ? CrmLinkStatus.Ambiguous : unit.CrmCompanyAmbiguous ? CrmLinkStatus.CompanyAmbiguous : CrmLinkStatus.Single;
            var linked = CollectionsContactLinker.Link(
                new SourceContact(unit.CustomerName.Trim(), CollectionsContactNormalizer.NormalizePhone(contactRow.Mobile), CollectionsContactNormalizer.NormalizeEmail(contactRow.Email)),
                new SourceContact(unit.CrmName?.Trim() ?? "", unit.CrmPhone ?? "", unit.CrmEmail ?? ""), crm);
            var due = instalments.Where(i => CollectionsDueMonth.IsDue(i.DueDate, Today)).Sum(i => i.RemainingAmount);
            var overdue = instalments.Where(i => CollectionsDueMonth.IsOverdue(i.DueDate, Today)).Sum(i => i.RemainingAmount);
            rows.Add(new(unit.CompanyId, unit.TenantId, unit.UnitCode, CollectionsTowerName.IsUsable(unit.TowerName, unit.TowerNumber) ? unit.TowerName!.Trim() : unit.TowerNumber ?? "",
                linked.NameSource == "Crm" ? linked.Name : unit.CustomerName, linked.Phone, linked.Email, due, overdue, due + overdue));
        }
        return rows;
    }

    [SqlFact]
    public async Task Campaigns_And_Receivables_ListTheSameUnitsContactsTowerNamesOrderAndFigures_ForIdenticalFilters()
    {
        await Seed();
        var run = Guid.NewGuid();
        await Exec($"INSERT dbo.CollectionsCrmUnitOwner (RunId, UnitKey, CompanyId, ProjectCode, UnitNumber, LeadId, LeadStatus, CrmCustomerId, CrmUnitId, ProjectId, FullName, Mobile, Email, PhoneNorm, EmailNorm) VALUES " +
                   $"('{run}', N'TP101-101', 4, N'P', N'101', 1, 8, 501, 1, 1, N'Dina CRM', N'0500000001', N'', N'+971500000001', N'dina.crm@example.test'); " +
                   $"UPDATE dbo.CollectionsCrmOwnerState SET CurrentRunId = '{run}', LastSuccessUtc = SYSUTCDATETIME(), LastAttemptStatus = 'Succeeded' WHERE Id = 1;");
        try
        {
            // The same wiring as the application: the campaign service gets the CRM owner link service over the real owner store.
            var options = new CollectionsOptions { Enabled = true };
            var links = new CollectionsUnitLinkService(new SqlCollectionsCrmOwnerStore(Config(), new ReceivablesSnapshotOptions()), new CollectionsCrmOwnersOptions { Enabled = true }, TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CollectionsUnitLinkService>.Instance);
            var service = new CollectionsCampaignAppService(options, new CollectionsCampaignOptions { FinancialSourceValidated = true }, new PactReceivablesOptions { Enabled = true, DefaultMinOutstandingAmount = 0m },
                new(options, new TigerCS.Tests.IdentityAndAccess.Fakes.FakeDepartmentRepository()), new(options, new TigerCS.Tests.Notifications.Fakes.FakeTimeProvider(new DateTime(2026, 10, 11, 5, 0, 0, DateTimeKind.Utc))),
                Source(), Microsoft.Extensions.Logging.Abstractions.NullLogger<CollectionsCampaignAppService>.Instance, links);
            var manager = new CollectionsCaller(Guid.NewGuid(), [Roles.CsManager], []);
            var towerId = await Scalar<int>("SELECT TOP 1 TowerId FROM dbo.CollectionsTowers WHERE TowerNumber = 127");
            var end = new DateOnly(2026, 10, 31);
            var filters = new (string Name, DateOnly From, DateOnly To, int? Company, int? Tower, string? Search, decimal? Min)[]
            {
                ("all units, no minimum", new(2000, 1, 1), end, null, null, null, null),
                ("minimum 100", new(2000, 1, 1), end, null, null, null, 100m),
                ("minimum 120 (strictly greater)", new(2000, 1, 1), end, null, null, null, 120m),
                ("company 4", new(2000, 1, 1), end, 4, null, null, null),
                ("company 32", new(2000, 1, 1), end, 32, null, null, null),
                ("tower 127", new(2000, 1, 1), end, null, towerId, null, null),
                ("search by name", new(2000, 1, 1), end, null, null, "Dina", null),
                ("search by CRM name", new(2000, 1, 1), end, null, null, "Dina CRM", null),
                ("search by unit", new(2000, 1, 1), end, null, null, "TP101", null),
                ("September only", new(2026, 9, 1), new(2026, 9, 30), null, null, null, null),
                ("October only", new(2026, 10, 1), end, null, null, null, null),
            };
            foreach (var stage in new[] { "CurrentMonthReminder", "FollowUpReminder", "OverdueReminder", "LegalNotice" })
                foreach (var f in filters)
                {
                    var expected = await ReceivablesRows(f.From, f.To, f.Company, f.Tower, f.Search, f.Min);
                    var campaign = (await service.PreviewAsync(manager, stage, companyId: f.Company, search: f.Search, pageSize: 100, dateFrom: f.From, dateTo: f.To, towerId: f.Tower, minTotal: f.Min)).Value!;
                    var actual = campaign.Items.Select(c => new ParityRow(c.CompanyId, c.TenantId, c.UnitCode, CollectionsTowerName.IsUsable(c.TowerName, c.TowerNumber) ? c.TowerName!.Trim() : c.TowerNumber ?? "",
                        c.CustomerName, c.Phone, c.Email, c.DueAmount, c.OverdueAmount, c.TotalAmount)).ToList();
                    Assert.Equal(expected.Count, actual.Count);
                    for (var i = 0; i < expected.Count; i++) Assert.True(expected[i] == actual[i], $"{stage} / {f.Name} row {i}: Receivables {expected[i]} vs Campaigns {actual[i]}");
                    Assert.Equal(expected.Count, campaign.TotalCount);
                    // Eligibility only decides readiness: a unit that is not eligible for this stage is still listed, with the same balances.
                    Assert.All(campaign.Items.Where(c => c.Status == "NotEligible"), c => Assert.Contains(c.Reason, new[] { "NoQualifyingBalance", "BelowThreshold" }));
                }
        }
        finally { await Exec("UPDATE dbo.CollectionsCrmOwnerState SET CurrentRunId = NULL WHERE Id = 1; DELETE FROM dbo.CollectionsCrmUnitOwner;"); }
    }
}

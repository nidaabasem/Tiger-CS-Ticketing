using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Import;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Infrastructure.Persistence.Migrations;

namespace TigerCS.Tests.WorkflowConfiguration.Import;

/// <summary>
/// The catalog import against a real relational database: what it creates,
/// that nothing unresolved goes live, that it links rather than duplicates
/// an existing request type, and that re-running it changes nothing.
/// </summary>
public class RequestTypeCatalogImporterTests
{
    private static readonly string[] ResolvedCodes =
    [
        "CS-GEN-001", "CS-GEN-003", "CS-CMP-002", "REG-CON-001", "REG-DLD-001", "COL-PAY-001", "COL-PAY-003",
        "COL-PAY-004", "HO-HND-001", "HO-HND-002", "HO-HND-003", "FM-UTL-001", "LCS-BKG-001", "LCS-MOV-001", "LCS-CHK-001"
    ];

    /// <summary>The four NOC request types the reference seed already has under Customer Service.</summary>
    private static readonly string[] LinkedCodes = ["REG-NOC-001", "REG-NOC-002", "REG-NOC-003", "HO-NOC-001"];

    private static async Task<(int RequestTypes, int Workflows, int Versions, int Steps, int Slas, int Approvals)> CountAsync(TigerCsDbContext db) =>
        (await db.RequestTypes.CountAsync(), await db.Workflows.CountAsync(), await db.WorkflowTemplates.CountAsync(),
         await db.Set<WorkflowTemplateStep>().CountAsync(), await db.RequestTypeSlaPolicies.CountAsync(),
         await db.RequestTypeApprovalRequirements.CountAsync());

    [Fact]
    public async Task A_dry_run_reports_the_plan_and_writes_nothing()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();
        await using (var before = db.CreateContext())
        {
            var counts = await CountAsync(before);

            var report = await db.ImportAsync(apply: false);

            Assert.False(report.Applied);
            Assert.Equal(15, report.Count(RequestTypeImportOutcome.CreatedActive));
            Assert.Equal(16, report.Count(RequestTypeImportOutcome.CreatedInactiveDraft));
            Assert.Equal(4, report.Count(RequestTypeImportOutcome.LinkedToExisting));
            Assert.Contains("DRY RUN", report.ToMarkdown());

            await using var after = db.CreateContext();
            Assert.Equal(counts, await CountAsync(after));
            Assert.False(await after.RequestTypes.AnyAsync(r => r.Code != null));
        }
    }

    [Fact]
    public async Task Import_creates_31_request_types_activates_only_the_resolved_ones_and_links_the_4_existing()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();

        var report = await db.ImportAsync();

        Assert.True(report.Applied);
        Assert.Equal(ResolvedCodes, report.Results.Where(r => r.Outcome == RequestTypeImportOutcome.CreatedActive).Select(r => r.Plan.Code));
        Assert.Equal(16, report.Count(RequestTypeImportOutcome.CreatedInactiveDraft));
        Assert.Equal(LinkedCodes, report.Results.Where(r => r.Outcome == RequestTypeImportOutcome.LinkedToExisting).Select(r => r.Plan.Code));
        Assert.Equal(0, report.Count(RequestTypeImportOutcome.Skipped));

        await using var context = db.CreateContext();
        var imported = await context.RequestTypes.Where(r => r.Code != null).ToListAsync();
        Assert.Equal(35, imported.Count);

        foreach (var requestType in imported.Where(r => !LinkedCodes.Contains(r.Code)))
        {
            var workflow = await context.Workflows.SingleAsync(w => w.WorkflowId == requestType.WorkflowId);
            var version = Assert.Single(await context.WorkflowTemplates.Where(t => t.WorkflowId == workflow.WorkflowId).ToListAsync());
            Assert.Equal(RequestTypeCatalogImporter.WorkflowCodeFor(requestType.Code!), workflow.Code);

            if (ResolvedCodes.Contains(requestType.Code))
            {
                Assert.True(requestType.IsActive);
                Assert.True(workflow.IsActive);
                Assert.Equal(WorkflowVersionStatus.Published, version.Status);
            }
            else
            {
                // Inactive draft: nothing a ticket could ever be created on.
                Assert.False(requestType.IsActive);
                Assert.False(workflow.IsActive);
                Assert.Equal(WorkflowVersionStatus.Draft, version.Status);
            }
        }
    }

    [Fact]
    public async Task An_imported_request_type_carries_the_catalogs_settings_on_the_existing_model()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();
        await db.ImportAsync();

        await using var context = db.CreateContext();
        var collections = await context.Departments.SingleAsync(d => d.Code == WorkflowReferenceData.CollectionsCode);
        var returned = await context.RequestTypes.SingleAsync(r => r.Code == "COL-PAY-002");

        Assert.Equal(collections.DepartmentId, returned.DepartmentId);
        Assert.Equal("Returned Cheque", returned.Name);
        Assert.Equal("Payments & Collection", returned.RequestGroup);
        Assert.Equal("Follow-up for a returned/bounced cheque.", returned.Description);
        Assert.Equal((byte)PriorityLevel.High, returned.DefaultPriorityId);
        Assert.Equal("""["Customer","Project","Unit","Cheque details"]""", returned.RequiredFieldsJson);
        Assert.Equal("""["Cheque copy / bank return document"]""", returned.RequiredDocumentsJson);
        Assert.True(returned.AllowReopen);
        Assert.False(returned.AllowAgentPriorityChange);
        Assert.False(returned.AllowPendingCustomer);
        Assert.False(returned.AllowPendingInternal);

        var sla = Assert.Single(await context.RequestTypeSlaPolicies.Where(p => p.RequestTypeId == returned.RequestTypeId).ToListAsync());
        Assert.Equal((byte)PriorityLevel.High, sla.PriorityId);
        Assert.Equal(SlaTriggerType.TicketCreated, sla.Trigger);
        Assert.Equal(2, sla.FirstResponseTargetValue);
        Assert.Equal(SlaDurationUnit.Hours, sla.EffectiveFirstResponseUnit);
        Assert.Equal(1, sla.ResolutionTargetValue);
        Assert.Equal(SlaDurationUnit.Days, sla.Unit);
        Assert.Null(sla.ResolutionMaximumValue);
        Assert.Equal(SlaClockBasis.BusinessHours, sla.ClockBasis);
        Assert.Null(sla.PausesOnPendingCustomer);

        // The conditional Collections approval is NOT turned into a
        // requirement; only the approved Reopen Approval rule applies.
        var approval = Assert.Single(await context.RequestTypeApprovalRequirements.Where(a => a.RequestTypeId == returned.RequestTypeId).ToListAsync());
        Assert.Equal(ApprovalType.ReopenApproval, approval.ApprovalType);
        Assert.Equal(Roles.CsManager, approval.TargetRoleName);
        Assert.False(approval.BlocksWorkUntilApproved);
    }

    [Fact]
    public async Task Unresolved_approvals_create_no_accounting_or_customer_service_approval_requirement()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();
        await db.ImportAsync();

        await using var context = db.CreateContext();
        var importedIds = await context.RequestTypes.Where(r => r.Code != null && !LinkedCodes.Contains(r.Code)).Select(r => r.RequestTypeId).ToListAsync();
        var approvalTypes = await context.RequestTypeApprovalRequirements
            .Where(a => importedIds.Contains(a.RequestTypeId))
            .Select(a => a.ApprovalType)
            .Distinct()
            .ToListAsync();

        Assert.Equal([ApprovalType.ReopenApproval], approvalTypes);
    }

    [Fact]
    public async Task Handoff_steps_are_stored_with_their_department_and_unconfirmed_ones_without()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();
        await db.ImportAsync();

        await using var context = db.CreateContext();
        var legal = await context.RequestTypes.SingleAsync(r => r.Code == "LEG-INQ-001");
        var customerService = await context.Departments.SingleAsync(d => d.Code == WorkflowReferenceData.CustomerServiceCode);
        var steps = (await context.WorkflowTemplates.SingleAsync(t => t.WorkflowId == legal.WorkflowId)).Steps;

        Assert.Equal(
            [WorkflowStepKind.Created, WorkflowStepKind.Assigned, WorkflowStepKind.Assigned, WorkflowStepKind.InProgress,
             WorkflowStepKind.Assigned, WorkflowStepKind.Resolved, WorkflowStepKind.Closed],
            steps.Select(s => s.Kind));
        Assert.Equal(customerService.DepartmentId, steps[1].DepartmentId);
        Assert.Null(steps.Single(s => s.Name == "Handoff to Legal").DepartmentId);
        Assert.Equal(customerService.DepartmentId, steps.Single(s => s.Name == "Return to Customer Service").DepartmentId);
    }

    [Fact]
    public async Task A_same_named_existing_request_type_is_linked_by_code_and_otherwise_left_untouched()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();

        RequestType before;
        List<RequestTypeSlaPolicy> slasBefore;
        await using (var context = db.CreateContext())
        {
            before = await context.RequestTypes.AsNoTracking().SingleAsync(r => r.Name == "NOC for Resale");
            slasBefore = await context.RequestTypeSlaPolicies.AsNoTracking().Where(p => p.RequestTypeId == before.RequestTypeId).ToListAsync();
        }

        var report = await db.ImportAsync();

        await using var after = db.CreateContext();
        var linked = await after.RequestTypes.SingleAsync(r => r.Code == "REG-NOC-001");
        Assert.Equal(before.RequestTypeId, linked.RequestTypeId);
        Assert.Equal(before.WorkflowId, linked.WorkflowId);
        Assert.Equal(before.DefaultPriorityId, linked.DefaultPriorityId);
        Assert.Equal(before.IsActive, linked.IsActive);
        Assert.Null(linked.Description);
        Assert.Equal(1, await after.RequestTypes.CountAsync(r => r.Name == "NOC for Resale"));
        Assert.Equal(
            slasBefore.Select(p => (p.PriorityId, p.ResolutionTargetValue, p.ResolutionMaximumValue)),
            (await after.RequestTypeSlaPolicies.Where(p => p.RequestTypeId == linked.RequestTypeId).ToListAsync())
                .Select(p => (p.PriorityId, p.ResolutionTargetValue, p.ResolutionMaximumValue)));

        var result = report.Results.Single(r => r.Plan.Code == "REG-NOC-001");
        Assert.Contains(result.Decisions, d => d.Area == CatalogDecisionArea.ExistingRequestType && d.Question.Contains("10–12 days"));
    }

    [Fact]
    public async Task Rerunning_the_import_creates_and_changes_nothing()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();
        await db.ImportAsync();

        (int, int, int, int, int, int) countsAfterFirst;
        await using (var context = db.CreateContext())
        {
            countsAfterFirst = await CountAsync(context);
        }

        var second = await db.ImportAsync();

        Assert.Equal(35, second.Count(RequestTypeImportOutcome.AlreadyImported));
        await using var after = db.CreateContext();
        Assert.Equal(countsAfterFirst, await CountAsync(after));
        Assert.Equal(15, await after.RequestTypes.CountAsync(r => r.Code != null && r.IsActive && !LinkedCodes.Contains(r.Code)));

        // Open decisions are still reported for the rows that remain inactive
        // and for the linked pre-existing ones; none for rows that went live.
        Assert.NotEmpty(second.Results.Single(r => r.Plan.Code == "SAL-INQ-001").Decisions);
        Assert.NotEmpty(second.Results.Single(r => r.Plan.Code == "REG-NOC-001").Decisions);
        Assert.Empty(second.Results.Single(r => r.Plan.Code == "CS-GEN-001").Decisions);
    }

    [Fact]
    public async Task A_rerun_never_overwrites_an_administration_change()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();
        await db.ImportAsync();

        await using (var context = db.CreateContext())
        {
            var general = await context.RequestTypes.SingleAsync(r => r.Code == "CS-GEN-001");
            general.Deactivate();
            general.Update("General Inquiry (renamed)", general.WorkflowId, (byte)PriorityLevel.Low, true, false, false, false, null);
            await context.SaveChangesAsync();
        }

        await db.ImportAsync();

        await using var after = db.CreateContext();
        var reloaded = await after.RequestTypes.SingleAsync(r => r.Code == "CS-GEN-001");
        Assert.False(reloaded.IsActive);
        Assert.Equal("General Inquiry (renamed)", reloaded.Name);
        Assert.Equal((byte)PriorityLevel.Low, reloaded.DefaultPriorityId);
        Assert.Equal(0, await after.RequestTypes.CountAsync(r => r.Name == "General Inquiry"));
    }

    [Fact]
    public async Task Rows_for_a_missing_department_are_skipped_and_created_by_a_rerun_once_it_exists()
    {
        using var db = await CatalogImportTestDb.CreateAsync(CatalogImportTestDb.FacilitiesManagement);

        var first = await db.ImportAsync();

        var skipped = first.Results.Where(r => r.Outcome == RequestTypeImportOutcome.Skipped).Select(r => r.Plan.Code).ToList();
        Assert.Equal(["LCS-TEN-001", "LCS-EJR-001", "LCS-BKG-001", "LCS-MOV-001", "LCS-CHK-001"], skipped);
        await using (var context = db.CreateContext())
        {
            Assert.False(await context.Departments.AnyAsync(d => d.Name == CatalogImportTestDb.LeasingCustomerServices));
            Assert.Equal(30, await context.RequestTypes.CountAsync(r => r.Code != null));

            context.Departments.Add(new Department(CatalogImportTestDb.LeasingCustomerServices, "LCS"));
            await context.SaveChangesAsync();
        }

        var second = await db.ImportAsync();

        Assert.Equal(30, second.Count(RequestTypeImportOutcome.AlreadyImported));
        Assert.Equal(3, second.Count(RequestTypeImportOutcome.CreatedActive));
        Assert.Equal(2, second.Count(RequestTypeImportOutcome.CreatedInactiveDraft));
        await using var after = db.CreateContext();
        Assert.Equal(35, await after.RequestTypes.CountAsync(r => r.Code != null));
    }

    [Fact]
    public async Task Keep_all_inactive_creates_every_row_as_an_inactive_draft()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();

        var report = await db.ImportAsync(activateResolved: false);

        Assert.Equal(0, report.Count(RequestTypeImportOutcome.CreatedActive));
        Assert.Equal(31, report.Count(RequestTypeImportOutcome.CreatedInactiveDraft));
        await using var context = db.CreateContext();
        Assert.False(await context.RequestTypes.AnyAsync(r => r.Code != null && r.IsActive && !LinkedCodes.Contains(r.Code)));
        Assert.False(await context.WorkflowTemplates.AnyAsync(t => t.Code.StartsWith(RequestTypeCatalogImporter.WorkflowCodePrefix) && t.Status != WorkflowVersionStatus.Draft));
    }

    [Fact]
    public async Task The_database_refuses_two_request_types_with_the_same_code_but_allows_any_number_without_one()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();
        await db.ImportAsync();

        await using var context = db.CreateContext();
        Assert.True(await context.RequestTypes.CountAsync(r => r.Code == null) > 1);

        var existing = await context.RequestTypes.SingleAsync(r => r.Code == "CS-GEN-001");
        var duplicate = new RequestType(existing.DepartmentId, "Another name", existing.WorkflowId, existing.DefaultPriorityId, false, false, false, true);
        duplicate.SetCatalogDetails("CS-GEN-001", null, null, null);
        context.RequestTypes.Add(duplicate);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task The_report_lists_what_was_added_what_stays_inactive_and_the_open_decisions()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();

        var markdown = (await db.ImportAsync(apply: false)).ToMarkdown();

        Assert.Contains("| CS-GEN-001 | General Inquiry | Customer Service | Medium | 4 bh / 1 bd | Created — active | 0 |", markdown);
        Assert.Contains("| SAL-INQ-001 |", markdown);
        Assert.Contains("## Decisions needed from the business", markdown);
        Assert.Contains("Confirm the handoff to Accounting", markdown);
        Assert.Contains("REG-NOC-001, REG-NOC-002, REG-NOC-003, HO-NOC-001", markdown);
    }

    // ---- the schema change -------------------------------------------------

    [Fact]
    public void Migration_is_additive_only_and_reversible()
    {
        var up = new AddRequestTypeCatalogImport().UpOperations;

        var columns = up.OfType<AddColumnOperation>().Select(c => (c.Table, c.Name, c.IsNullable)).ToList();
        Assert.Equal(
            [
                ("WorkflowTemplateSteps", "DepartmentId", true),
                ("RequestTypeSlaPolicies", "FirstResponseUnit", true),
                ("RequestTypes", "Code", true),
                ("RequestTypes", "Description", true),
                ("RequestTypes", "RequestGroup", true),
                ("RequestTypes", "RequiredDocumentsJson", true)
            ],
            columns);

        var codeIndex = Assert.Single(up.OfType<CreateIndexOperation>(), i => i.Name == "IX_RequestTypes_Code");
        Assert.True(codeIndex.IsUnique);
        Assert.Equal("[Code] IS NOT NULL", codeIndex.Filter);

        var fk = Assert.Single(up.OfType<AddForeignKeyOperation>());
        Assert.Equal(("WorkflowTemplateSteps", "Departments", ReferentialAction.Restrict), (fk.Table, fk.PrincipalTable, fk.OnDelete));

        Assert.Empty(up.OfType<DropTableOperation>());
        Assert.Empty(up.OfType<DropColumnOperation>());
        Assert.Empty(up.OfType<AlterColumnOperation>());
        Assert.Empty(up.OfType<SqlOperation>());
        Assert.Empty(up.OfType<InsertDataOperation>());
        Assert.Empty(up.OfType<UpdateDataOperation>());
        Assert.Empty(up.OfType<DeleteDataOperation>());

        var down = new AddRequestTypeCatalogImport().DownOperations;
        Assert.Equal(6, down.OfType<DropColumnOperation>().Count());
        Assert.Single(down.OfType<DropForeignKeyOperation>());
    }

    [Fact]
    public void The_import_command_names_the_real_migration()
    {
        var migrationId = typeof(AddRequestTypeCatalogImport)
            .GetCustomAttributes(typeof(MigrationAttribute), false)
            .Cast<MigrationAttribute>()
            .Single()
            .Id;

        Assert.Equal(RequestTypeCatalogCommand.RequiredMigration, migrationId);
    }
}

/// <summary>The hand-run entry point: a dry run unless --apply is given, and never triggered without its switch.</summary>
public class RequestTypeCatalogCommandTests
{
    private static ServiceProvider Services(string databaseName) =>
        new ServiceCollection()
            .AddDbContext<TigerCsDbContext>(o => o.UseInMemoryDatabase(databaseName))
            .BuildServiceProvider();

    private static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        foreach (var level in Enum.GetValues<PriorityLevel>())
        {
            context.Priorities.Add(new Priority((byte)level, level.ToString(), (byte)level));
        }

        context.Departments.Add(new Department("Customer Service", WorkflowReferenceData.CustomerServiceCode));
        await context.SaveChangesAsync();
        await WorkflowReferenceData.SeedAsync(context);
    }

    private static async Task<int> CodedRequestTypesAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TigerCsDbContext>().RequestTypes.CountAsync(r => r.Code != null);
    }

    [Fact]
    public void Only_the_explicit_switch_requests_the_import()
    {
        Assert.False(RequestTypeCatalogCommand.IsRequested([]));
        Assert.False(RequestTypeCatalogCommand.IsRequested(["--apply"]));
        Assert.True(RequestTypeCatalogCommand.IsRequested([RequestTypeCatalogCommand.Switch]));
    }

    [Fact]
    public async Task Without_apply_the_command_prints_the_report_and_writes_nothing()
    {
        using var services = Services(Guid.NewGuid().ToString());
        await SeedAsync(services);
        var output = new StringWriter();

        var exitCode = await RequestTypeCatalogCommand.RunAsync(services, [RequestTypeCatalogCommand.Switch], output);

        Assert.Equal(0, exitCode);
        Assert.Contains("DRY RUN", output.ToString());
        Assert.Equal(0, await CodedRequestTypesAsync(services));
    }

    [Fact]
    public async Task With_apply_the_command_imports_and_a_second_run_changes_nothing()
    {
        using var services = Services(Guid.NewGuid().ToString());
        await SeedAsync(services);

        await RequestTypeCatalogCommand.RunAsync(services, [RequestTypeCatalogCommand.Switch, RequestTypeCatalogCommand.ApplySwitch], new StringWriter());
        var afterFirst = await CodedRequestTypesAsync(services);
        var second = new StringWriter();
        await RequestTypeCatalogCommand.RunAsync(services, [RequestTypeCatalogCommand.Switch, RequestTypeCatalogCommand.ApplySwitch], second);

        // The reference seed has no Facilities Management / Leasing departments, so their 9 rows are skipped.
        Assert.Equal(26, afterFirst);
        Assert.Equal(afterFirst, await CodedRequestTypesAsync(services));
        Assert.Contains("| Already imported (unchanged) | 26 |", second.ToString());
    }
}

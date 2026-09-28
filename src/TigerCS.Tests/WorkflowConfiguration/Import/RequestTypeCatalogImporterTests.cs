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
/// that nothing goes live and no existing request type is touched unless
/// explicitly asked, that settings the workbook omits are inherited rather
/// than switched off, and that re-running it changes nothing.
/// </summary>
public class RequestTypeCatalogImporterTests
{
    private static readonly string[] ResolvedCodes =
    [
        "CS-GEN-001", "CS-GEN-003", "CS-CMP-002", "REG-CON-001", "REG-DLD-001", "COL-PAY-001", "COL-PAY-003",
        "COL-PAY-004", "HO-HND-001", "HO-HND-002", "HO-HND-003", "FM-UTL-001", "LCS-BKG-001", "LCS-MOV-001", "LCS-CHK-001"
    ];

    /// <summary>The four NOC request types the reference seed already has under Customer Service.</summary>
    private static readonly string[] ExistingCodes = ["REG-NOC-001", "REG-NOC-002", "REG-NOC-003", "HO-NOC-001"];
    private static readonly string[] ExistingNames = ["NOC for Resale", "NOC for Golden Visa", "NOC for Mortgage", "NOC for Handover"];

    private static async Task<(int RequestTypes, int Workflows, int Versions, int Steps, int Slas, int Approvals)> CountAsync(TigerCsDbContext db) =>
        (await db.RequestTypes.CountAsync(), await db.Workflows.CountAsync(), await db.WorkflowTemplates.CountAsync(),
         await db.Set<WorkflowTemplateStep>().CountAsync(), await db.RequestTypeSlaPolicies.CountAsync(),
         await db.RequestTypeApprovalRequirements.CountAsync());

    /// <summary>Everything about the four existing NOC types, as comparable values.</summary>
    private static async Task<string> FingerprintExistingAsync(TigerCsDbContext db)
    {
        var types = await db.RequestTypes.AsNoTracking().Where(r => ExistingNames.Contains(r.Name)).OrderBy(r => r.RequestTypeId).ToListAsync();
        var ids = types.Select(t => t.RequestTypeId).ToList();
        var slas = await db.RequestTypeSlaPolicies.AsNoTracking().Where(p => ids.Contains(p.RequestTypeId)).OrderBy(p => p.RequestTypeSlaPolicyId).ToListAsync();
        var approvals = await db.RequestTypeApprovalRequirements.AsNoTracking().Where(a => ids.Contains(a.RequestTypeId)).OrderBy(a => a.RequestTypeApprovalRequirementId).ToListAsync();
        return string.Join("\n",
            types.Select(t => $"{t.RequestTypeId}|{t.Name}|{t.Code}|{t.DepartmentId}|{t.WorkflowId}|{t.DefaultPriorityId}|{t.AllowAgentPriorityChange}|{t.AllowPendingCustomer}|{t.AllowReopen}|{t.IsActive}|{t.Description}")
                .Concat(slas.Select(p => $"{p.RequestTypeId}|{p.PriorityId}|{p.Trigger}|{p.Unit}|{p.FirstResponseUnit}|{p.FirstResponseTargetValue}|{p.ResolutionTargetValue}|{p.ResolutionMaximumValue}|{p.ClockBasis}"))
                .Concat(approvals.Select(a => $"{a.RequestTypeId}|{a.ApprovalType}|{a.TargetKind}|{a.TargetRoleName}|{a.TargetDepartmentId}|{a.IsActive}")));
    }

    // ---- dry runs ---------------------------------------------------------

    [Fact]
    public async Task A_dry_run_reports_the_plan_and_writes_nothing()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();
        await using var before = db.CreateContext();
        var counts = await CountAsync(before);

        var report = await db.ImportAsync(new RequestTypeCatalogImportOptions(CatalogImportTestDb.Now));

        Assert.False(report.Applied);
        Assert.Equal(0, report.Count(RequestTypeImportOutcome.CreatedActive));
        Assert.Equal(31, report.Count(RequestTypeImportOutcome.CreatedInactiveDraft));
        Assert.Equal(4, report.Count(RequestTypeImportOutcome.ExistingUnchanged));
        Assert.Contains("DRY RUN", report.ToMarkdown());

        await using var after = db.CreateContext();
        Assert.Equal(counts, await CountAsync(after));
        Assert.False(await after.RequestTypes.AnyAsync(r => r.Code != null));
    }

    [Fact]
    public async Task A_dry_run_works_before_the_migration_reading_only_existing_columns()
    {
        using var db = await CatalogImportTestDb.CreateBeforeMigrationAsync();
        await using (var context = db.CreateContext())
        {
            // The columns really are absent — any query touching one would fail.
            var columns = await context.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('RequestTypes')").ToListAsync();
            Assert.Contains("Name", columns);
            Assert.DoesNotContain("Code", columns);
            await Assert.ThrowsAnyAsync<Exception>(() => context.RequestTypes.AnyAsync(r => r.Code != null));
        }

        var report = await db.ImportAsync(new RequestTypeCatalogImportOptions(CatalogImportTestDb.Now, SchemaApplied: false));

        Assert.Equal(31, report.Count(RequestTypeImportOutcome.CreatedInactiveDraft));
        Assert.Equal(4, report.Count(RequestTypeImportOutcome.ExistingUnchanged));
        Assert.Contains("not applied", report.ToMarkdown());
        Assert.All(report.Results.Where(r => r.Existing is not null), r => Assert.NotEmpty(r.Existing!.Slas));
    }

    [Fact]
    public async Task Applying_before_the_migration_is_refused()
    {
        using var db = await CatalogImportTestDb.CreateBeforeMigrationAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.ImportAsync(new RequestTypeCatalogImportOptions(CatalogImportTestDb.Now, Apply: true, SchemaApplied: false)));
    }

    // ---- the first import: all inactive, existing untouched ----------------

    [Fact]
    public async Task The_first_import_creates_31_inactive_drafts_and_leaves_the_4_existing_types_byte_for_byte_unchanged()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();
        string fingerprintBefore;
        await using (var context = db.CreateContext())
        {
            fingerprintBefore = await FingerprintExistingAsync(context);
        }

        var report = await db.ImportAsync();

        Assert.True(report.Applied);
        Assert.Equal(0, report.Count(RequestTypeImportOutcome.CreatedActive));
        Assert.Equal(31, report.Count(RequestTypeImportOutcome.CreatedInactiveDraft));
        Assert.Equal(ExistingCodes, report.Results.Where(r => r.Outcome == RequestTypeImportOutcome.ExistingUnchanged).Select(r => r.Plan.Code));
        Assert.Equal(0, report.Count(RequestTypeImportOutcome.Skipped));

        await using var after = db.CreateContext();
        Assert.Equal(fingerprintBefore, await FingerprintExistingAsync(after));
        Assert.Equal(1, await after.RequestTypes.CountAsync(r => r.Name == "NOC for Resale"));

        var created = await after.RequestTypes.Where(r => r.Code != null).ToListAsync();
        Assert.Equal(31, created.Count);
        Assert.All(created, r => Assert.False(r.IsActive));
        foreach (var requestType in created)
        {
            var workflow = await after.Workflows.SingleAsync(w => w.WorkflowId == requestType.WorkflowId);
            var version = Assert.Single(await after.WorkflowTemplates.Where(t => t.WorkflowId == workflow.WorkflowId).ToListAsync());
            Assert.Equal(RequestTypeCatalogImporter.WorkflowCodeFor(requestType.Code!), workflow.Code);
            Assert.False(workflow.IsActive);
            Assert.Equal(WorkflowVersionStatus.Draft, version.Status);
        }
    }

    [Fact]
    public async Task The_existing_types_are_reported_with_a_current_vs_workbook_comparison()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();

        var report = await db.ImportAsync();

        var resale = report.Results.Single(r => r.Plan.Code == "REG-NOC-001").Existing!;
        Assert.True(resale.IsActive);
        Assert.Equal(PriorityLevel.Medium, resale.DefaultPriority);
        Assert.True(resale.AllowPendingCustomer);
        Assert.True(resale.AllowAgentPriorityChange);
        Assert.Contains(resale.Slas, s => s.StartsWith("Medium:", StringComparison.Ordinal) && s.Contains("10–12 days"));
        Assert.Contains(resale.Slas, s => s.StartsWith("High:", StringComparison.Ordinal) && s.Contains("2–4 days"));
        // The reference seed's pre-existing ReopenApproval row is reported
        // factually and left alone — never as a gating approval, and never
        // described as part of the approved Reopen rule.
        Assert.Empty(resale.Approvals);
        Assert.StartsWith("pre-existing ReopenApproval requirement row", resale.ReopenRequestRoute);
        Assert.Contains("Pending Customer (optional)", resale.Steps);

        var markdown = report.ToMarkdown();
        Assert.Contains("## Existing request types — current vs workbook", markdown);
        Assert.Contains("### REG-NOC-001 — NOC for Resale (Customer Service)", markdown);
        Assert.Contains("| Approvals gating the work | none | Conditional: Registration Supervisor / Authorized Approver |", markdown);
        Assert.Contains("| Reopen | allowed — direct Reopen by CS Agent, CS Supervisor or CS Manager; System Administrator through the central override. "
            + "Note: pre-existing ReopenApproval requirement row", markdown);
        Assert.Contains("| Yes — direct Reopen by CS Agent, CS Supervisor or CS Manager; System Administrator through the central override |", markdown);
        Assert.DoesNotContain("plus Reopen Approval", markdown);
        Assert.DoesNotContain("requests from roles without direct Reopen", markdown);
    }

    [Fact]
    public async Task Linking_existing_types_is_opt_in_and_writes_only_the_code()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();
        RequestType before;
        await using (var context = db.CreateContext())
        {
            before = await context.RequestTypes.AsNoTracking().SingleAsync(r => r.Name == "NOC for Resale");
        }

        var report = await db.ImportAsync(CatalogImportTestDb.FirstImport with { LinkExisting = true });

        Assert.Equal(4, report.Count(RequestTypeImportOutcome.LinkedToExisting));
        await using var after = db.CreateContext();
        var linked = await after.RequestTypes.SingleAsync(r => r.Code == "REG-NOC-001");
        Assert.Equal(
            (before.RequestTypeId, before.WorkflowId, before.DefaultPriorityId, before.IsActive, before.AllowPendingCustomer, before.AllowAgentPriorityChange),
            (linked.RequestTypeId, linked.WorkflowId, linked.DefaultPriorityId, linked.IsActive, linked.AllowPendingCustomer, linked.AllowAgentPriorityChange));
        Assert.Null(linked.Description);
    }

    // ---- settings the workbook does not give --------------------------------

    [Fact]
    public async Task Created_types_inherit_Pending_Customer_and_flag_agent_priority_change()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();

        var report = await db.ImportAsync();

        await using var context = db.CreateContext();
        foreach (var requestType in await context.RequestTypes.Where(r => r.Code != null).ToListAsync())
        {
            var version = await context.WorkflowTemplates.SingleAsync(t => t.WorkflowId == requestType.WorkflowId);
            // Pending Customer stays available — not switched off because the workbook is silent.
            Assert.True(requestType.AllowPendingCustomer);
            Assert.True(version.AllowsPendingCustomer);
            Assert.True(WorkflowCapabilities.Resolve(version, requestType).CanGoPendingCustomer);
            // Recorded as today's unrestricted behaviour, and flagged.
            Assert.Equal(RequestTypeCatalogImporter.UndecidedAgentPriorityChangePlaceholder, requestType.AllowAgentPriorityChange);
        }

        Assert.All(
            report.Results.Where(r => r.Outcome == RequestTypeImportOutcome.CreatedInactiveDraft),
            r => Assert.Contains(RequestTypeCatalogImporter.AgentPriorityChangeDecision, r.Decisions));
        Assert.Contains("Configuration with no default to inherit", report.ToMarkdown());
    }

    [Fact]
    public async Task A_supplied_priority_change_answer_is_used_and_no_longer_flagged()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();

        var report = await db.ImportAsync(CatalogImportTestDb.FirstImport with { AllowAgentPriorityChange = false });

        Assert.DoesNotContain(report.Results, r => r.Decisions.Contains(RequestTypeCatalogImporter.AgentPriorityChangeDecision));
        await using var context = db.CreateContext();
        Assert.All(await context.RequestTypes.Where(r => r.Code != null).ToListAsync(), r => Assert.False(r.AllowAgentPriorityChange));
    }

    [Fact]
    public async Task Every_open_decision_is_persisted_with_its_created_request_type_and_none_with_a_resolved_one()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();

        var report = await db.ImportAsync(CatalogImportTestDb.ActivatingImport);

        await using var context = db.CreateContext();
        foreach (var result in report.Results.Where(r => r.Outcome is RequestTypeImportOutcome.CreatedActive or RequestTypeImportOutcome.CreatedInactiveDraft))
        {
            var stored = await context.RequestTypeCatalogDecisions.Where(d => d.RequestTypeId == result.RequestTypeId).ToListAsync();
            Assert.Equal(result.Decisions.Select(d => (d.Area.ToString(), d.Question)), stored.Select(d => (d.Area, d.Question)));
            Assert.All(stored, d => Assert.False(d.IsResolved));
            Assert.Equal(result.Outcome == RequestTypeImportOutcome.CreatedActive, stored.Count == 0);
        }

        // The import never enables runtime enforcement — that is an explicit administrator step.
        Assert.False(await context.RequestTypes.AnyAsync(r => r.ConfigurationEnforced));
    }

    [Fact]
    public async Task An_imported_request_type_carries_the_workbooks_settings_on_the_existing_model()
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

        var sla = Assert.Single(await context.RequestTypeSlaPolicies.Where(p => p.RequestTypeId == returned.RequestTypeId).ToListAsync());
        Assert.Equal((byte)PriorityLevel.High, sla.PriorityId);
        Assert.Equal(SlaTriggerType.TicketCreated, sla.Trigger);
        Assert.Equal(2, sla.FirstResponseTargetValue);
        Assert.Equal(SlaDurationUnit.Hours, sla.EffectiveFirstResponseUnit);
        Assert.Equal(1, sla.ResolutionTargetValue);
        Assert.Equal(SlaDurationUnit.Days, sla.Unit);
        Assert.Equal(SlaClockBasis.BusinessHours, sla.ClockBasis);
        Assert.Null(sla.PausesOnPendingCustomer);

        // Neither the conditional Collections approval nor any Reopen
        // Approval is created: Reopen stays the approved direct rule.
        Assert.Empty(await context.RequestTypeApprovalRequirements.Where(a => a.RequestTypeId == returned.RequestTypeId).ToListAsync());
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

        Assert.Equal(customerService.DepartmentId, steps[1].DepartmentId);
        Assert.Null(steps.Single(s => s.Name == "Handoff to Legal").DepartmentId);
        Assert.Equal(customerService.DepartmentId, steps.Single(s => s.Name == "Return to Customer Service").DepartmentId);
    }

    // ---- activation (a later, explicit decision) ------------------------------

    [Fact]
    public async Task Activation_only_happens_when_asked_and_only_for_rows_with_no_open_decision()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();

        // Asked, but the priority-change question is unanswered: still nothing goes live.
        var unanswered = await db.ImportAsync(new RequestTypeCatalogImportOptions(CatalogImportTestDb.Now, ActivateResolved: true));
        Assert.Equal(0, unanswered.Count(RequestTypeImportOutcome.CreatedActive));

        // With the real workbook nothing can activate: every SLA is in
        // business days, whose meaning is an open decision.
        Assert.Equal(0, (await db.ImportAsync(CatalogImportTestDb.ActivatingImport with { Apply = false })).Count(RequestTypeImportOutcome.CreatedActive));

        // A workbook variant giving the same rows' SLAs in business hours has
        // no open question left once priority change is answered.
        var hoursVariant = RequestTypeCatalog.Load()
            .Select(r => r with { ResolutionSla = r.ResolutionSla.Replace("business days", "business hours").Replace("business day", "business hours") })
            .ToList();
        var report = await db.ImportAsync(hoursVariant, CatalogImportTestDb.ActivatingImport);

        Assert.Equal(ResolvedCodes, report.Results.Where(r => r.Outcome == RequestTypeImportOutcome.CreatedActive).Select(r => r.Plan.Code));
        await using var context = db.CreateContext();
        foreach (var code in ResolvedCodes)
        {
            var requestType = await context.RequestTypes.SingleAsync(r => r.Code == code);
            Assert.True(requestType.IsActive);
            Assert.Equal(WorkflowVersionStatus.Published, (await context.WorkflowTemplates.SingleAsync(t => t.WorkflowId == requestType.WorkflowId)).Status);
        }
    }

    // ---- reruns and administration edits ------------------------------------

    [Fact]
    public async Task Rerunning_the_import_creates_and_changes_nothing()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();
        await db.ImportAsync();

        (int, int, int, int, int, int) countsAfterFirst;
        string existingAfterFirst;
        await using (var context = db.CreateContext())
        {
            countsAfterFirst = await CountAsync(context);
            existingAfterFirst = await FingerprintExistingAsync(context);
        }

        var second = await db.ImportAsync();

        Assert.Equal(31, second.Count(RequestTypeImportOutcome.AlreadyImported));
        Assert.Equal(4, second.Count(RequestTypeImportOutcome.ExistingUnchanged));
        await using var after = db.CreateContext();
        Assert.Equal(countsAfterFirst, await CountAsync(after));
        Assert.Equal(existingAfterFirst, await FingerprintExistingAsync(after));
        Assert.False(await after.RequestTypes.AnyAsync(r => r.Code != null && r.IsActive));

        // Open decisions are still reported on a rerun.
        Assert.NotEmpty(second.Results.Single(r => r.Plan.Code == "SAL-INQ-001").Decisions);
        Assert.NotEmpty(second.Results.Single(r => r.Plan.Code == "REG-NOC-001").Decisions);
    }

    [Fact]
    public async Task A_rerun_even_one_asking_for_activation_never_overwrites_an_administration_change()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();
        await db.ImportAsync();

        await using (var context = db.CreateContext())
        {
            // An administrator finishes CS-GEN-001 by hand: renames, re-prioritizes,
            // turns Pending Customer off and edits its SLA.
            var general = await context.RequestTypes.SingleAsync(r => r.Code == "CS-GEN-001");
            general.Update("General Inquiry (renamed)", general.WorkflowId, (byte)PriorityLevel.Low, false, false, false, false, null);
            var sla = await context.RequestTypeSlaPolicies.SingleAsync(p => p.RequestTypeId == general.RequestTypeId);
            sla.Update(SlaTriggerType.TicketCreated, SlaDurationUnit.Days, 1, null, 3, null, false, SlaClockBasis.BusinessHours, null, null, null, true);
            await context.SaveChangesAsync();
        }

        var rerun = await db.ImportAsync(CatalogImportTestDb.ActivatingImport);

        Assert.Equal(RequestTypeImportOutcome.AlreadyImported, rerun.Results.Single(r => r.Plan.Code == "CS-GEN-001").Outcome);
        await using var after = db.CreateContext();
        var reloaded = await after.RequestTypes.SingleAsync(r => r.Code == "CS-GEN-001");
        Assert.False(reloaded.IsActive); // not activated by the rerun
        Assert.Equal("General Inquiry (renamed)", reloaded.Name);
        Assert.Equal((byte)PriorityLevel.Low, reloaded.DefaultPriorityId);
        Assert.False(reloaded.AllowPendingCustomer);
        Assert.False(reloaded.AllowReopen);
        var reloadedSla = await after.RequestTypeSlaPolicies.SingleAsync(p => p.RequestTypeId == reloaded.RequestTypeId);
        Assert.Equal(3, reloadedSla.ResolutionTargetValue);
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
            Assert.Equal(26, await context.RequestTypes.CountAsync(r => r.Code != null));

            context.Departments.Add(new Department(CatalogImportTestDb.LeasingCustomerServices, "LCS"));
            await context.SaveChangesAsync();
        }

        var second = await db.ImportAsync();

        Assert.Equal(26, second.Count(RequestTypeImportOutcome.AlreadyImported));
        Assert.Equal(5, second.Count(RequestTypeImportOutcome.CreatedInactiveDraft));
        await using var after = db.CreateContext();
        Assert.Equal(31, await after.RequestTypes.CountAsync(r => r.Code != null));
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
    public async Task The_report_states_that_workflow_definitions_run_only_once_enforcement_is_enabled()
    {
        using var db = await CatalogImportTestDb.CreateWithAllCatalogDepartmentsAsync();

        var markdown = (await db.ImportAsync(new RequestTypeCatalogImportOptions(CatalogImportTestDb.Now))).ToMarkdown();

        Assert.Contains("## Workflow definitions", markdown);
        Assert.Contains("enforced at runtime only once an administrator turns on configuration enforcement", markdown);
        Assert.Contains("Transfer action (CS Manager only", markdown);
        Assert.Contains("Activation: **off**", markdown);
        Assert.Contains("left completely unchanged", markdown);
        Assert.Contains("Confirm the handoff to Accounting", markdown);
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
    public void The_runtime_enforcement_migration_is_additive_only_and_reversible()
    {
        var up = new AddConfiguredRuntimeEnforcement().UpOperations;

        Assert.Equal(
            [("Tickets", "CurrentWorkflowStepId", true), ("RequestTypes", "ConfigurationEnforced", false)],
            up.OfType<AddColumnOperation>().Select(c => (c.Table, c.Name, c.IsNullable)));
        Assert.Equal(false, up.OfType<AddColumnOperation>().Single(c => c.Name == "ConfigurationEnforced").DefaultValue);
        Assert.Equal("RequestTypeCatalogDecisions", Assert.Single(up.OfType<CreateTableOperation>()).Name);

        Assert.Empty(up.OfType<DropTableOperation>());
        Assert.Empty(up.OfType<DropColumnOperation>());
        Assert.Empty(up.OfType<AlterColumnOperation>());
        Assert.Empty(up.OfType<SqlOperation>());
        Assert.Empty(up.OfType<InsertDataOperation>());
        Assert.Empty(up.OfType<UpdateDataOperation>());
        Assert.Empty(up.OfType<DeleteDataOperation>());

        var down = new AddConfiguredRuntimeEnforcement().DownOperations;
        Assert.Single(down.OfType<DropTableOperation>());
        Assert.Equal(2, down.OfType<DropColumnOperation>().Count());
    }

    [Fact]
    public void The_import_command_names_the_real_migration()
    {
        var migrationId = typeof(AddConfiguredRuntimeEnforcement)
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
    public async Task Apply_without_an_explicit_activation_choice_is_refused_and_writes_nothing()
    {
        using var services = Services(Guid.NewGuid().ToString());
        await SeedAsync(services);
        var output = new StringWriter();

        var exitCode = await RequestTypeCatalogCommand.RunAsync(services, [RequestTypeCatalogCommand.Switch, RequestTypeCatalogCommand.ApplySwitch], output);

        Assert.Equal(3, exitCode);
        Assert.Contains(RequestTypeCatalogCommand.KeepAllInactiveSwitch, output.ToString());
        Assert.Equal(0, await CodedRequestTypesAsync(services));
        Assert.Equal(3, await RequestTypeCatalogCommand.RunAsync(services,
            [RequestTypeCatalogCommand.Switch, RequestTypeCatalogCommand.ApplySwitch,
             RequestTypeCatalogCommand.KeepAllInactiveSwitch, RequestTypeCatalogCommand.ActivateResolvedSwitch], new StringWriter()));
    }

    [Fact]
    public async Task The_first_UAT_command_imports_everything_inactive_and_a_second_run_changes_nothing()
    {
        using var services = Services(Guid.NewGuid().ToString());
        await SeedAsync(services);
        string[] firstImport = [RequestTypeCatalogCommand.Switch, RequestTypeCatalogCommand.ApplySwitch, RequestTypeCatalogCommand.KeepAllInactiveSwitch];

        Assert.Equal(0, await RequestTypeCatalogCommand.RunAsync(services, firstImport, new StringWriter()));
        var afterFirst = await CodedRequestTypesAsync(services);
        var second = new StringWriter();
        await RequestTypeCatalogCommand.RunAsync(services, firstImport, second);

        // The reference seed has no Facilities Management / Leasing departments
        // (9 rows skipped) and already has the 4 NOC types (left unchanged).
        Assert.Equal(22, afterFirst);
        Assert.Equal(afterFirst, await CodedRequestTypesAsync(services));
        Assert.Contains("| Already imported (unchanged) | 22 |", second.ToString());
        Assert.Contains("| Existing — left unchanged | 4 |", second.ToString());
        using var scope = services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<TigerCsDbContext>().RequestTypes.AnyAsync(r => r.Code != null && r.IsActive));
    }
}

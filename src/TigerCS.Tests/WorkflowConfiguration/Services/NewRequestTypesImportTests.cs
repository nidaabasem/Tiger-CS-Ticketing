using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;
using TigerCS.Infrastructure.Persistence;
using Outcome = TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed.NewRequestTypeImportOutcome;

namespace TigerCS.Tests.WorkflowConfiguration.Services;

/// <summary>
/// The UAT baseline import of the 35 Customer Service request types:
/// faithful to the workbook, active, idempotent, additive only, and never
/// guessing configuration the workbook or the model cannot express.
/// </summary>
public class NewRequestTypesImportTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 8, 0, 0, DateTimeKind.Utc);

    private static readonly string[] ExistingCsNocNames =
        ["NOC for Resale", "NOC for Golden Visa", "NOC for Mortgage", "NOC for Handover"];

    // ---- source fidelity --------------------------------------------------

    [Fact]
    public void The_embedded_export_is_the_committed_workbook_cell_for_cell()
    {
        var root = NewRequestTypesSqlScript.RepositoryRoot();
        var workbook = ReadWorkbook(Path.Combine(root, "docs", "business-review", "TigerCS_New_Request_Types_Business_Review.xlsx"));
        var tsv = File.ReadAllLines(Path.Combine(root, "docs", "business-review", "TigerCS_New_Request_Types_Business_Review.tsv"))
            .Select(l => l.Split('\t'))
            .ToList();

        // Header on row 5, the 35 request types on rows 6–40 (the sheet's own COUNTIF ranges).
        var expected = Enumerable.Range(5, 36).Select(r => Enumerable.Range(0, 17).Select(c => workbook.GetValueOrDefault((r, c), string.Empty)).ToArray()).ToList();
        Assert.Equal(expected.Count, tsv.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i], tsv[i]);
        }
    }

    [Fact]
    public void Catalog_holds_exactly_the_35_workbook_request_codes_each_once()
    {
        var rows = NewRequestTypesBusinessReview.Rows();

        Assert.Equal(NewRequestTypesBusinessReview.ExpectedRowCount, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r.RequestCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(rows.Count, rows.Select(r => (r.Department, r.Name)).Distinct().Count());
    }

    [Fact]
    public void Every_workbook_value_normalizes_without_guessing()
    {
        var rows = NewRequestTypesBusinessReview.Rows();

        // Would throw on an unknown value.
        Assert.All(rows, r =>
        {
            _ = r.OwningDepartment;
            _ = r.Priority;
            _ = r.FirstResponseBusinessHours;
            _ = r.ResolutionBusinessDays;
            _ = r.IsConditionalApproval;
        });

        Assert.All(rows, r => Assert.True(r.AllowTransferFlag));
        Assert.All(rows, r => Assert.True(r.AllowReopenFlag));
        Assert.Equal(10, rows.Count(r => r.IsConditionalApproval));
        Assert.All(rows.Where(r => !r.IsConditionalApproval), r => Assert.Equal(string.Empty, r.ApprovalRole));

        Assert.Equal(
            ["HO-HND-004", "FM-MNT-001", "FM-COM-001"],
            rows.Where(r => r.ResolutionBusinessDays is null).Select(r => r.RequestCode));

        Assert.Equal(PriorityLevel.Medium, NewRequestTypesBusinessReview.MapPriority("Normal"));
        Assert.Throws<FormatException>(() => NewRequestTypesBusinessReview.MapPriority("Urgent"));
        Assert.Equal(1, NewRequestTypesBusinessReview.ParseResolutionDays("Same business day"));
        Assert.Throws<FormatException>(() => NewRequestTypesBusinessReview.ParseResolutionDays("3 calendar days"));
    }

    [Fact]
    public void Every_translated_workflow_passes_the_designers_publish_validation()
    {
        foreach (var row in NewRequestTypesBusinessReview.Rows())
        {
            var version = BuildVersion(row);
            var errors = version.Validate().Where(i => i.Severity == WorkflowValidationSeverity.Error).ToList();
            Assert.True(errors.Count == 0, $"{row.RequestCode}: {string.Join(" | ", errors.Select(e => e.Message))}");
            Assert.All(row.Steps, s => Assert.False(WorkflowStepKinds.IsLegacyOnly(s.Kind)));

            // The only approval steps are Accounting's part of a NOC, as the
            // EXISTING Accounting Approval — no approval is invented.
            var approvalSteps = row.Steps.Where(s => s.Kind == WorkflowStepKind.WaitingForApproval).ToList();
            if (AccountingNocCodes.Contains(row.RequestCode))
            {
                var approval = Assert.Single(approvalSteps);
                Assert.Equal(ApprovalType.AccountingApproval, approval.ApprovalType);
            }
            else
            {
                Assert.Empty(approvalSteps);
            }

            Assert.All(row.Steps.Where(s => s.Kind != WorkflowStepKind.WaitingForApproval), s => Assert.Null(s.ApprovalType));
        }
    }

    private static readonly string[] AccountingNocCodes = ["REG-NOC-001", "REG-NOC-002", "REG-NOC-003", "HO-NOC-001"];

    private static readonly string[] ManualSupportingCodes =
        ["BRK-COM-001", "BRK-CHK-001", "SAL-INQ-001", "LEG-INQ-001", "REC-HR-001", "REC-MKT-001", "FM-SVC-001", "HO-HND-004"];

    [Fact]
    public void Every_value_fits_its_column()
    {
        foreach (var row in NewRequestTypesBusinessReview.Rows())
        {
            Assert.True(row.WorkflowCode.Length <= Workflow.CodeMaxLength, row.RequestCode);
            Assert.True(row.Name.Length <= 100, row.RequestCode);
            Assert.True(row.WorkflowName.Length <= 100, row.RequestCode);
            Assert.True(row.WorkflowDescription.Length <= 500, row.RequestCode);
            Assert.True(row.RequiredFieldsJson.Length <= 2000, row.RequestCode);
            Assert.All(row.Steps, s => Assert.True(s.Name.Length <= 100, row.RequestCode));
            Assert.NotEmpty(JsonSerializer.Deserialize<string[]>(row.RequiredFieldsJson)!);
        }
    }

    [Fact]
    public void The_uat_sql_script_carries_exactly_the_catalogs_rows()
    {
        var path = Path.Combine(NewRequestTypesSqlScript.RepositoryRoot(), NewRequestTypesSqlScript.FileName);
        var script = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        var rendered = NewRequestTypesSqlScript.RenderDataBlock();

        var start = script.IndexOf(NewRequestTypesSqlScript.BeginMarker, StringComparison.Ordinal);
        var end = script.IndexOf(NewRequestTypesSqlScript.EndMarker, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Generated-data markers not found in the UAT script.");
        var committed = script[start..(end + NewRequestTypesSqlScript.EndMarker.Length)];

        if (committed != rendered && Environment.GetEnvironmentVariable("TIGERCS_REGENERATE_UAT_SQL") == "1")
        {
            File.WriteAllText(path, script.Replace(committed, rendered, StringComparison.Ordinal));
            return;
        }

        Assert.Equal(rendered, committed);
    }

    // ---- import behaviour -------------------------------------------------

    [Fact]
    public async Task Imports_every_row_active_and_reuses_the_exact_name_matches()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        var activeBefore = await db.RequestTypes.CountAsync(r => r.IsActive);

        var result = await NewRequestTypesImporter.ImportAsync(db, Now);

        Assert.Equal(35, result.Rows.Count);
        Assert.Equal(31, result.Count(Outcome.Created));
        // Same department + name as the seeded Customer Service NOC request types.
        Assert.Equal(["REG-NOC-001", "REG-NOC-002", "REG-NOC-003", "HO-NOC-001"], Codes(result, Outcome.ExistingRequestTypeReused));
        Assert.Equal(activeBefore + 31, await db.RequestTypes.CountAsync(r => r.IsActive));

        // All 35 workbook rows are available: 31 new + 4 reused, all active.
        var ids = result.Rows.Select(r => r.RequestTypeId!.Value).ToList();
        Assert.Equal(35, await db.RequestTypes.CountAsync(r => ids.Contains(r.RequestTypeId) && r.IsActive));

        // "Complaint" is not an exact match for "Complaint Handling": imported, and reported.
        Assert.Equal(Outcome.Created, Row(result, "CS-CMP-001").Outcome);
        Assert.Equal("Complaint Handling", Row(result, "CS-CMP-001").SimilarExistingRequestType);
    }

    [Fact]
    public async Task Missing_owning_departments_are_created_once()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();

        var result = await NewRequestTypesImporter.ImportAsync(db, Now);

        Assert.Equal(["Facilities Management", "Leasing Customer Services"], result.CreatedDepartments);
        var lcs = await db.Departments.SingleAsync(d => d.Code == "LCS");
        Assert.Equal("Leasing Customer Services", lcs.Name);
        Assert.Equal(5, await db.RequestTypes.CountAsync(r => r.DepartmentId == lcs.DepartmentId && r.IsActive));

        var again = await NewRequestTypesImporter.ImportAsync(db, Now);
        Assert.Empty(again.CreatedDepartments);
        Assert.Single(await db.Departments.Where(d => d.Name == "Facilities Management").ToListAsync());
        Assert.Single(await db.Departments.Where(d => d.Name == "Leasing Customer Services").ToListAsync());
    }

    [Fact]
    public async Task Existing_owning_departments_are_found_by_exact_name_under_another_code()
    {
        await using var db = await CreateWithFacilitiesManagementAsync();
        db.Departments.Add(new Department("Leasing Customer Services", "LEASECS"));
        await db.SaveChangesAsync();
        var departmentCount = await db.Departments.CountAsync();

        var result = await NewRequestTypesImporter.ImportAsync(db, Now);

        Assert.Empty(result.CreatedDepartments);
        Assert.Equal(departmentCount, await db.Departments.CountAsync());
        var leasing = await db.Departments.SingleAsync(d => d.Code == "LEASECS");
        Assert.Equal(5, await db.RequestTypes.CountAsync(r => r.DepartmentId == leasing.DepartmentId));
    }

    [Fact]
    public async Task Supporting_teams_are_manual_steps_that_stay_with_the_owning_department()
    {
        foreach (var row in NewRequestTypesBusinessReview.Rows())
        {
            var supporting = row.Steps.Where(s => s.Role == NewRequestTypesBusinessReview.StepRole.ManualSupporting).ToList();
            if (ManualSupportingCodes.Contains(row.RequestCode))
            {
                Assert.NotEmpty(supporting);
            }
            else
            {
                Assert.Empty(supporting);
            }

            // Informational owning-department work: never a queue, assignment
            // or approval step, so nothing can read it as a transfer.
            Assert.All(supporting, s =>
            {
                Assert.Equal(WorkflowStepKind.InProgress, s.Kind);
                Assert.Null(s.ApprovalType);
                Assert.EndsWith(" (manual supporting step)", s.Name, StringComparison.Ordinal);
            });

            // No step invents a transfer the workbook does not state.
            Assert.DoesNotContain(row.Steps, s => s.Name.StartsWith("Transfer", StringComparison.Ordinal));
        }

        var handoverNoc = NewRequestTypesBusinessReview.Rows().Single(r => r.RequestCode == "HO-NOC-001");
        var handoverAgent = Assert.Single(handoverNoc.Steps, s => s.Role == NewRequestTypesBusinessReview.StepRole.BusinessDecisionRequired);
        Assert.Equal("Handover Agent", handoverAgent.Name);

        await using var db = await CreateWithFacilitiesManagementAsync();
        var result = await NewRequestTypesImporter.ImportAsync(db, Now);
        Assert.Equal(Outcome.Created, Row(result, "LEG-INQ-001").Outcome);
        Assert.Equal(Outcome.Created, Row(result, "FM-SVC-001").Outcome);
    }

    [Fact]
    public async Task Created_request_types_carry_the_workbook_configuration()
    {
        await using var db = await CreateWithFacilitiesManagementAsync();
        var result = await NewRequestTypesImporter.ImportAsync(db, Now);

        foreach (var outcome in result.Rows.Where(r => r.Outcome is Outcome.Created))
        {
            var row = NewRequestTypesBusinessReview.Rows().Single(r => r.RequestCode == outcome.RequestCode);
            var requestType = await db.RequestTypes.SingleAsync(r => r.RequestTypeId == outcome.RequestTypeId);
            var workflow = await db.Workflows.SingleAsync(w => w.WorkflowId == requestType.WorkflowId);
            var department = await db.Departments.SingleAsync(d => d.DepartmentId == requestType.DepartmentId);

            Assert.True(requestType.IsActive);
            Assert.Equal(row.Name, requestType.Name);
            Assert.Equal(row.OwningDepartment.Code, department.Code);
            Assert.Equal((byte)row.Priority, requestType.DefaultPriorityId);
            Assert.True(requestType.AllowReopen);
            Assert.False(requestType.AllowAgentPriorityChange);
            Assert.False(requestType.AllowPendingCustomer);
            Assert.False(requestType.AllowPendingInternal);
            Assert.Equal(row.RequiredFieldsJson, requestType.RequiredFieldsJson);
            Assert.Equal(row.RequestCode, workflow.Code);
            Assert.Equal(row.WorkflowDescription, workflow.Description);

            var version = await db.WorkflowTemplates.Include(t => t.Steps).SingleAsync(t => t.WorkflowId == workflow.WorkflowId);
            Assert.Equal(1, version.VersionNumber);
            Assert.Equal(row.RequestCode, version.Code);
            Assert.Equal(WorkflowVersionStatus.Published, version.Status);
            Assert.Equal(row.Steps.Select(s => (s.Name, s.Kind, s.IsOptional)), version.Steps.Select(s => (s.Name, s.Kind, s.IsOptional)));

            var sla = await db.RequestTypeSlaPolicies.Where(p => p.RequestTypeId == requestType.RequestTypeId).ToListAsync();
            if (row.ResolutionBusinessDays is { } days)
            {
                var policy = Assert.Single(sla);
                Assert.Equal((byte)row.Priority, policy.PriorityId);
                Assert.Equal(SlaTriggerType.TicketCreated, policy.Trigger);
                Assert.Equal(SlaDurationUnit.Days, policy.Unit);
                Assert.Equal(SlaClockBasis.BusinessHours, policy.ClockBasis);
                Assert.Equal(days, policy.ResolutionTargetValue);
                Assert.Null(policy.ResolutionMaximumValue);
                Assert.Null(policy.FirstResponseTargetValue);
                Assert.True(outcome.SlaConfigured);
            }
            else
            {
                Assert.Empty(sla);
                Assert.False(outcome.SlaConfigured);
            }
        }

        // No approval requirement is invented for the "Conditional" rows.
        var created = result.Rows.Where(r => r.Outcome is Outcome.Created).Select(r => r.RequestTypeId!.Value).ToList();
        Assert.False(await db.RequestTypeApprovalRequirements.AnyAsync(a => created.Contains(a.RequestTypeId)));
    }

    [Fact]
    public async Task Existing_configuration_is_never_modified()
    {
        await using var db = await CreateWithFacilitiesManagementAsync();
        db.Departments.Add(new Department("Leasing Customer Services", "LCS"));
        await db.SaveChangesAsync();
        var requestTypesBefore = await Snapshot(db);
        var approvalsBefore = await db.RequestTypeApprovalRequirements.AsNoTracking()
            .Select(r => new { r.RequestTypeApprovalRequirementId, r.RequestTypeId, r.ApprovalType, r.TargetKind, r.TargetRoleName, r.TargetDepartmentId, r.BlocksWorkUntilApproved, r.IsActive })
            .ToListAsync();
        var departmentsBefore = await db.Departments.AsNoTracking().Select(d => new { d.DepartmentId, d.Name, d.Code, d.IsActive }).ToListAsync();
        var settingsBefore = await db.DepartmentWorkflowSettings.AsNoTracking().CountAsync();

        await NewRequestTypesImporter.ImportAsync(db, Now);

        var requestTypesAfter = await Snapshot(db);
        Assert.All(requestTypesBefore, before => Assert.Contains(before, requestTypesAfter));

        // Every existing approval requirement is untouched; the only additions
        // are the existing Accounting Approval on the four reused NOC request
        // types, targeted at the Accounting department.
        var approvalsAfter = await db.RequestTypeApprovalRequirements.AsNoTracking()
            .Select(r => new { r.RequestTypeApprovalRequirementId, r.RequestTypeId, r.ApprovalType, r.TargetKind, r.TargetRoleName, r.TargetDepartmentId, r.BlocksWorkUntilApproved, r.IsActive })
            .ToListAsync();
        Assert.All(approvalsBefore, before => Assert.Contains(before, approvalsAfter));
        var added = approvalsAfter.Except(approvalsBefore).ToList();
        var accounting = await db.Departments.SingleAsync(d => d.Code == WorkflowReferenceData.AccountingCode);
        var csDepartment = await db.Departments.SingleAsync(d => d.Code == WorkflowReferenceData.CustomerServiceCode);
        var nocIds = await db.RequestTypes
            .Where(r => r.DepartmentId == csDepartment.DepartmentId && ExistingCsNocNames.Contains(r.Name))
            .Select(r => r.RequestTypeId).ToListAsync();
        Assert.Equal(nocIds.Order(), added.Select(a => a.RequestTypeId).Order());
        Assert.All(added, a =>
        {
            Assert.Equal(ApprovalType.AccountingApproval, a.ApprovalType);
            Assert.Equal(ApprovalTargetKind.Department, a.TargetKind);
            Assert.Equal(accounting.DepartmentId, a.TargetDepartmentId);
            Assert.True(a.IsActive);
        });
        Assert.Equal(departmentsBefore, await db.Departments.AsNoTracking().Select(d => new { d.DepartmentId, d.Name, d.Code, d.IsActive }).ToListAsync());
        Assert.Equal(settingsBefore, await db.DepartmentWorkflowSettings.CountAsync());

        // The reused NOC request types keep their own workflows.
        var cs = await db.Departments.SingleAsync(d => d.Code == WorkflowReferenceData.CustomerServiceCode);
        foreach (var name in ExistingCsNocNames)
        {
            var existing = await db.RequestTypes.SingleAsync(r => r.DepartmentId == cs.DepartmentId && r.Name == name);
            Assert.True(existing.IsActive);
            var workflow = await db.Workflows.SingleAsync(w => w.WorkflowId == existing.WorkflowId);
            Assert.Equal(WorkflowReferenceData.WithPendingTemplateCode, workflow.Code);
        }
    }

    [Fact]
    public async Task A_deactivated_Accounting_Approval_is_left_as_the_operator_set_it()
    {
        await using var db = await CreateWithFacilitiesManagementAsync();
        var cs = await db.Departments.SingleAsync(d => d.Code == WorkflowReferenceData.CustomerServiceCode);
        var accounting = await db.Departments.SingleAsync(d => d.Code == WorkflowReferenceData.AccountingCode);
        var resale = await db.RequestTypes.SingleAsync(r => r.DepartmentId == cs.DepartmentId && r.Name == "NOC for Resale");
        db.RequestTypeApprovalRequirements.Add(RequestTypeApprovalRequirement.ForDepartment(
            resale.RequestTypeId, ApprovalType.AccountingApproval, accounting.DepartmentId, isActive: false));
        await db.SaveChangesAsync();

        var result = await NewRequestTypesImporter.ImportAsync(db, Now);

        Assert.Empty(Row(result, "REG-NOC-001").ApprovalRequirementsAdded ?? []);
        var requirement = await db.RequestTypeApprovalRequirements.SingleAsync(
            r => r.RequestTypeId == resale.RequestTypeId && r.ApprovalType == ApprovalType.AccountingApproval);
        Assert.False(requirement.IsActive);
        Assert.Equal([ApprovalType.AccountingApproval], Row(result, "REG-NOC-002").ApprovalRequirementsAdded);
        Assert.False(result.ApprovalDepartmentMissing);
    }

    [Fact]
    public async Task Running_twice_creates_nothing_the_second_time()
    {
        await using var db = await CreateWithFacilitiesManagementAsync();
        var first = await NewRequestTypesImporter.ImportAsync(db, Now);
        var counts = await Counts(db);

        var second = await NewRequestTypesImporter.ImportAsync(db, Now.AddDays(1));

        Assert.Equal(counts, await Counts(db));
        foreach (var row in first.Rows)
        {
            var again = Row(second, row.RequestCode);
            if (row.Outcome is Outcome.Created)
            {
                Assert.Equal(Outcome.AlreadyImported, again.Outcome);
                Assert.Equal(row.RequestTypeId, again.RequestTypeId);
                Assert.Equal(row.SlaConfigured, again.SlaConfigured);
            }
            else
            {
                Assert.Equal(row.Outcome, again.Outcome);
            }
        }
    }

    [Fact]
    public async Task Administration_edits_made_during_uat_are_never_overwritten()
    {
        await using var db = await CreateWithFacilitiesManagementAsync();
        var first = await NewRequestTypesImporter.ImportAsync(db, Now);
        var id = Row(first, "CS-GEN-001").RequestTypeId;

        var imported = await db.RequestTypes.SingleAsync(r => r.RequestTypeId == id);
        imported.Update("General Customer Inquiry", imported.WorkflowId, imported.DefaultPriorityId, imported.AllowAgentPriorityChange,
            imported.AllowPendingCustomer, imported.AllowPendingInternal, imported.AllowReopen, imported.RequiredFieldsJson);
        imported.Deactivate();
        await db.SaveChangesAsync();
        var counts = await Counts(db);

        var second = await NewRequestTypesImporter.ImportAsync(db, Now);

        Assert.Equal(Outcome.AlreadyImported, Row(second, "CS-GEN-001").Outcome);
        Assert.Equal(counts, await Counts(db));
        var after = await db.RequestTypes.AsNoTracking().SingleAsync(r => r.RequestTypeId == id);
        Assert.Equal("General Customer Inquiry", after.Name);
        Assert.False(after.IsActive);
    }

    [Fact]
    public async Task A_same_named_request_type_created_outside_the_import_is_reused_untouched()
    {
        await using var db = await CreateWithFacilitiesManagementAsync();
        var cs = await db.Departments.SingleAsync(d => d.Code == WorkflowReferenceData.CustomerServiceCode);
        var standard = await db.Workflows.SingleAsync(w => w.Code == WorkflowReferenceData.StandardTemplateCode);
        db.RequestTypes.Add(new RequestType(cs.DepartmentId, "General Inquiry", standard.WorkflowId, (byte)PriorityLevel.High,
            allowAgentPriorityChange: true, allowPendingCustomer: false, allowPendingInternal: false, allowReopen: false));
        await db.SaveChangesAsync();

        var result = await NewRequestTypesImporter.ImportAsync(db, Now);

        Assert.Equal(Outcome.ExistingRequestTypeReused, Row(result, "CS-GEN-001").Outcome);
        Assert.False(await db.Workflows.AnyAsync(w => w.Code == "CS-GEN-001"));
        var untouched = await db.RequestTypes.SingleAsync(r => r.DepartmentId == cs.DepartmentId && r.Name == "General Inquiry");
        Assert.Equal(untouched.RequestTypeId, Row(result, "CS-GEN-001").RequestTypeId);
        Assert.Equal((byte)PriorityLevel.High, untouched.DefaultPriorityId);
        Assert.False(untouched.AllowReopen);
    }

    [Fact]
    public async Task A_workflow_already_using_a_request_code_is_left_alone()
    {
        await using var db = await CreateWithFacilitiesManagementAsync();
        db.Workflows.Add(new Workflow("CS-GEN-002", "Someone else's workflow", null, Now));
        await db.SaveChangesAsync();

        var result = await NewRequestTypesImporter.ImportAsync(db, Now);

        Assert.Equal(Outcome.SkippedCodeInUse, Row(result, "CS-GEN-002").Outcome);
        Assert.False(await db.RequestTypes.AnyAsync(r => r.Name == "Office Hours / Contact Information"));
    }

    [Fact]
    public async Task Is_transactional_and_idempotent_on_a_relational_database()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TigerCsDbContext>().UseSqlite(connection).Options;

        await using (var db = new TigerCsDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            foreach (var level in Enum.GetValues<PriorityLevel>())
            {
                db.Priorities.Add(new Priority((byte)level, level.ToString(), (byte)level));
            }

            db.Departments.Add(new Department("Customer Service", WorkflowReferenceData.CustomerServiceCode));
            db.Departments.Add(new Department("Registration", WorkflowReferenceData.RegistrationCode));
            db.Departments.Add(new Department("Collections", WorkflowReferenceData.CollectionsCode));
            db.Departments.Add(new Department("Handover", WorkflowReferenceData.HandoverCode));
            await db.SaveChangesAsync();
            await WorkflowReferenceData.SeedAsync(db);

            var first = await NewRequestTypesImporter.ImportAsync(db, Now);
            Assert.Equal(["Facilities Management", "Leasing Customer Services"], first.CreatedDepartments);
            Assert.Equal(31, first.Count(Outcome.Created));
        }

        await using (var db = new TigerCsDbContext(options))
        {
            var counts = await Counts(db);
            var second = await NewRequestTypesImporter.ImportAsync(db, Now);
            Assert.Equal(31, second.Count(Outcome.AlreadyImported));
            Assert.Empty(second.CreatedDepartments);
            Assert.Equal(counts, await Counts(db));
        }
    }

    [Fact]
    public async Task Import_creates_exact_request_type_routing_once_and_preserves_inactive_and_historical_categories()
    {
        await using var db = await CreateWithFacilitiesManagementAsync();
        var cs = await db.Departments.SingleAsync(d => d.Code == WorkflowReferenceData.CustomerServiceCode);
        var inactive = new Category("General Inquiry", cs.DepartmentId, isActive: false);
        var historical = new Category("Historic inquiry", cs.DepartmentId);
        db.Categories.AddRange(inactive, historical);
        await db.SaveChangesAsync();
        var inactiveId = inactive.CategoryId;
        var historicalId = historical.CategoryId;
        await NewRequestTypesImporter.ImportAsync(db, Now);
        var categories = await db.Categories.ToListAsync();
        foreach (var requestType in await db.RequestTypes.Where(r => r.IsActive).ToListAsync())
        {
            Assert.Single(categories, c => c.DepartmentId == requestType.DepartmentId && c.Name == requestType.Name);
        }
        Assert.False((await db.Categories.SingleAsync(c => c.CategoryId == inactiveId)).IsActive);
        Assert.Equal("Historic inquiry", (await db.Categories.SingleAsync(c => c.CategoryId == historicalId)).Name);
        var before = categories.OrderBy(c => c.CategoryId).Select(c => $"{c.CategoryId}|{c.DepartmentId}|{c.Name}|{c.IsActive}").ToList();
        await NewRequestTypesImporter.ImportAsync(db, Now.AddDays(1));
        var after = (await db.Categories.OrderBy(c => c.CategoryId).ToListAsync())
            .Select(c => $"{c.CategoryId}|{c.DepartmentId}|{c.Name}|{c.IsActive}").ToList();
        Assert.Equal(before, after);
    }

    // ---- helpers ----------------------------------------------------------

    private static async Task<TigerCsDbContext> CreateWithFacilitiesManagementAsync()
    {
        var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        db.Departments.Add(new Department("Facilities Management", "FM"));
        await db.SaveChangesAsync();
        return db;
    }

    private static WorkflowTemplate BuildVersion(NewRequestTypesBusinessReview.Row row)
    {
        var version = new WorkflowTemplate(0, 1, row.WorkflowCode, row.WorkflowName, row.WorkflowDescription, false, false, false, Now, null);
        foreach (var step in row.Steps)
        {
            version.AppendStep(step.Name, step.Kind, step.IsOptional, step.ApprovalType);
        }

        return version;
    }

    private static List<string> Codes(NewRequestTypeImportResult result, Outcome outcome) =>
        result.Rows.Where(r => r.Outcome == outcome).Select(r => r.RequestCode).ToList();

    private static NewRequestTypeImportRowResult Row(NewRequestTypeImportResult result, string code) =>
        result.Rows.Single(r => r.RequestCode == code);

    private static async Task<(int RequestTypes, int Workflows, int Versions, int Steps, int Sla, int Approvals, int Departments)> Counts(TigerCsDbContext db) =>
        (await db.RequestTypes.CountAsync(), await db.Workflows.CountAsync(), await db.WorkflowTemplates.CountAsync(),
         await db.Set<WorkflowTemplateStep>().CountAsync(), await db.RequestTypeSlaPolicies.CountAsync(),
         await db.RequestTypeApprovalRequirements.CountAsync(), await db.Departments.CountAsync());

    private static async Task<List<string>> Snapshot(TigerCsDbContext db) =>
        (await db.RequestTypes.AsNoTracking().ToListAsync())
            .Select(r => $"{r.RequestTypeId}|{r.DepartmentId}|{r.Name}|{r.WorkflowId}|{r.DefaultPriorityId}|{r.AllowAgentPriorityChange}|{r.AllowPendingCustomer}|{r.AllowPendingInternal}|{r.AllowReopen}|{r.RequiredFieldsJson}|{r.IsActive}")
            .ToList();

    /// <summary>Reads the first worksheet of an .xlsx into (row, column) → text, both 0-based except rows, which keep the sheet's 1-based numbering.</summary>
    private static Dictionary<(int Row, int Column), string> ReadWorkbook(string path)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using var zip = ZipFile.OpenRead(path);

        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } sharedEntry)
        {
            using var stream = sharedEntry.Open();
            shared = XDocument.Load(stream).Root!.Elements(ns + "si")
                .Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value)))
                .ToList();
        }

        using var sheetStream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var cells = new Dictionary<(int, int), string>();
        foreach (var cell in XDocument.Load(sheetStream).Descendants(ns + "c"))
        {
            var reference = cell.Attribute("r")!.Value;
            var letters = new string(reference.TakeWhile(char.IsLetter).ToArray());
            var row = int.Parse(reference[letters.Length..], System.Globalization.CultureInfo.InvariantCulture);
            var column = letters.Aggregate(0, (acc, ch) => (acc * 26) + (ch - 'A' + 1)) - 1;

            var type = cell.Attribute("t")?.Value;
            var value = type switch
            {
                "s" => shared[int.Parse(cell.Element(ns + "v")!.Value, System.Globalization.CultureInfo.InvariantCulture)],
                "inlineStr" => string.Concat(cell.Descendants(ns + "t").Select(t => t.Value)),
                _ => cell.Element(ns + "v")?.Value
            };

            if (value is not null)
            {
                cells[(row, column)] = value;
            }
        }

        return cells;
    }
}

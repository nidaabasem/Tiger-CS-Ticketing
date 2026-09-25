using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;
using TigerCS.Infrastructure.Persistence;
using Outcome = TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed.NewRequestTypeImportOutcome;

namespace TigerCS.Tests.WorkflowConfiguration.Services;

/// <summary>
/// The UAT import of the 35 proposed new request types: faithful to the
/// business-review workbook, idempotent, additive only, inactive, and never
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
        Assert.All(rows, r => Assert.Equal(string.Empty, r.BusinessDecision));
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
            ["CS-GEN-002", "HO-HND-004", "FM-MNT-001", "FM-COM-001"],
            rows.Where(r => r.ResolutionBusinessDays is null).Select(r => r.RequestCode));

        Assert.Equal(PriorityLevel.Medium, NewRequestTypesBusinessReview.MapPriority("Normal"));
        Assert.Throws<FormatException>(() => NewRequestTypesBusinessReview.MapPriority("Urgent"));
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
            Assert.DoesNotContain(row.Steps, s => s.Kind == WorkflowStepKind.WaitingForApproval);
        }
    }

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
    public async Task Without_department_creation_rows_of_missing_owning_departments_are_blocked_and_reported()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        var departmentCount = await db.Departments.CountAsync();

        var result = await NewRequestTypesImporter.ImportAsync(db, Now);

        Assert.Equal(35, result.Rows.Count);
        Assert.Equal(departmentCount, await db.Departments.CountAsync());
        Assert.Equal(
            [("Facilities Management", OwningDepartmentResolution.Missing), ("Leasing Customer Services", OwningDepartmentResolution.Missing)],
            result.OwningDepartments.Select(d => (d.Name, d.Resolution)));
        Assert.Equal(
            ["CS-GEN-001", "CS-GEN-002", "CS-GEN-003", "CS-CMP-001", "CS-CMP-002", "REG-CON-001", "REG-DLD-001",
             "COL-PAY-001", "COL-PAY-002", "COL-PAY-003", "COL-PAY-004", "HO-HND-001", "HO-HND-002", "HO-HND-003", "REC-OTH-001"],
            Codes(result, Outcome.Created));
        Assert.Equal(
            ["HO-HND-004", "BRK-COM-001", "BRK-CHK-001", "SAL-INQ-001", "LEG-INQ-001", "REC-HR-001", "REC-MKT-001"],
            Codes(result, Outcome.CreatedWithDraftWorkflow));
        // Same department + name as the seeded Customer Service NOC request types.
        Assert.Equal(["REG-NOC-001", "REG-NOC-002", "REG-NOC-003", "HO-NOC-001"], Codes(result, Outcome.SkippedExistingRequestType));
        Assert.Equal(
            ["FM-MNT-001", "FM-UTL-001", "FM-COM-001", "FM-SVC-001", "LCS-TEN-001", "LCS-EJR-001", "LCS-BKG-001", "LCS-MOV-001", "LCS-CHK-001"],
            Codes(result, Outcome.SkippedOwningDepartmentMissing));
        Assert.All(result.Rows.Where(r => r.Outcome == Outcome.SkippedOwningDepartmentMissing), r => Assert.True(r.BlockedByMissingOwningDepartment));

        Assert.Equal(["Facilities Management"], Row(result, "HO-HND-004").UnresolvedDestinations);
        Assert.Equal(["Responsible Finance"], Row(result, "FM-SVC-001").UnresolvedDestinations);
    }

    [Fact]
    public async Task Confirmed_owning_departments_are_created_when_genuinely_missing_and_asked_to()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();

        var result = await NewRequestTypesImporter.ImportAsync(db, Now, createMissingOwningDepartments: true);

        Assert.Equal(
            [("Facilities Management", "FM", OwningDepartmentResolution.Created), ("Leasing Customer Services", "LCS", OwningDepartmentResolution.Created)],
            result.OwningDepartments.Select(d => (d.Name, d.Code, d.Resolution)));
        Assert.Single(await db.Departments.Where(d => d.Name == "Facilities Management").ToListAsync());
        Assert.Single(await db.Departments.Where(d => d.Name == "Leasing Customer Services").ToListAsync());

        Assert.Equal(24, result.Count(Outcome.Created));
        Assert.Equal(
            ["FM-SVC-001", "BRK-COM-001", "BRK-CHK-001", "SAL-INQ-001", "LEG-INQ-001", "REC-HR-001", "REC-MKT-001"],
            Codes(result, Outcome.CreatedWithDraftWorkflow));
        Assert.Equal(4, result.Count(Outcome.SkippedExistingRequestType));
        Assert.DoesNotContain(result.Rows, r => r.BlockedByMissingOwningDepartment);
        // Created before any row is processed, so the Handover hand-off to FM resolves in the same run.
        Assert.Equal(Outcome.Created, Row(result, "HO-HND-004").Outcome);

        var lcs = await db.Departments.SingleAsync(d => d.Code == "LCS");
        Assert.Equal(5, await db.RequestTypes.CountAsync(r => r.DepartmentId == lcs.DepartmentId && !r.IsActive));
    }

    [Fact]
    public async Task Existing_owning_departments_are_used_and_never_duplicated()
    {
        await using var db = await CreateWithFacilitiesManagementAsync();
        db.Departments.Add(new Department("Leasing Customer Services", "LEASECS"));
        await db.SaveChangesAsync();
        var departmentCount = await db.Departments.CountAsync();

        var result = await NewRequestTypesImporter.ImportAsync(db, Now, createMissingOwningDepartments: true);

        Assert.Equal(departmentCount, await db.Departments.CountAsync());
        Assert.All(result.OwningDepartments, d => Assert.Equal(OwningDepartmentResolution.Existing, d.Resolution));
        var leasing = await db.Departments.SingleAsync(d => d.Code == "LEASECS");
        Assert.Equal(5, await db.RequestTypes.CountAsync(r => r.DepartmentId == leasing.DepartmentId));
    }

    [Fact]
    public async Task A_similarly_named_department_blocks_creation_instead_of_duplicating_it()
    {
        await using var db = await CreateWithFacilitiesManagementAsync();
        db.Departments.Add(new Department("Leasing", "LEAS"));
        await db.SaveChangesAsync();
        var departmentCount = await db.Departments.CountAsync();

        var result = await NewRequestTypesImporter.ImportAsync(db, Now, createMissingOwningDepartments: true);

        Assert.Equal(departmentCount, await db.Departments.CountAsync());
        var leasing = result.OwningDepartments.Single(d => d.Name == "Leasing Customer Services");
        Assert.Equal(OwningDepartmentResolution.NearMatch, leasing.Resolution);
        Assert.Equal(["Leasing (LEAS)"], leasing.NearMatches);
        Assert.Equal(
            ["LCS-TEN-001", "LCS-EJR-001", "LCS-BKG-001", "LCS-MOV-001", "LCS-CHK-001"],
            Codes(result, Outcome.SkippedOwningDepartmentNearMatch));
    }

    [Fact]
    public async Task Pending_handoff_destinations_keep_their_flows_draft_even_if_a_same_named_department_exists()
    {
        await using var db = await CreateWithFacilitiesManagementAsync();
        db.Departments.Add(new Department("Legal", "LGL"));
        db.Departments.Add(new Department("Finance", "FIN"));
        await db.SaveChangesAsync();

        var result = await NewRequestTypesImporter.ImportAsync(db, Now);

        Assert.Equal(Outcome.CreatedWithDraftWorkflow, Row(result, "LEG-INQ-001").Outcome);
        Assert.Equal(["Legal"], Row(result, "LEG-INQ-001").UnresolvedDestinations);
        Assert.Equal(Outcome.CreatedWithDraftWorkflow, Row(result, "FM-SVC-001").Outcome);

        // Documented manual hand-offs are department work on the owning
        // department, never a queue/assignment step that implies a transfer.
        foreach (var row in NewRequestTypesBusinessReview.Rows())
        {
            foreach (var step in row.Steps.Where(s => s.Destination is { RepresentationPending: true }))
            {
                Assert.Equal(WorkflowStepKind.InProgress, step.Kind);
                Assert.StartsWith("Manual handoff to ", step.Name, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Every_row_carries_its_uat_review_flags()
    {
        await using var db = await CreateWithFacilitiesManagementAsync();

        var result = await NewRequestTypesImporter.ImportAsync(db, Now);

        Assert.Equal(
            ["CS-CMP-001", "REG-NOC-001", "REG-NOC-002", "REG-NOC-003", "COL-PAY-002", "HO-NOC-001", "LCS-TEN-001", "LCS-EJR-001", "BRK-COM-001", "LEG-INQ-001"],
            result.Rows.Where(r => r.ApprovalDecisionRequired).Select(r => r.RequestCode));
        Assert.Equal(
            ["CS-GEN-002", "HO-HND-004", "FM-MNT-001", "FM-COM-001"],
            result.Rows.Where(r => r.ResolutionSlaDecisionRequired).Select(r => r.RequestCode));

        // Exact-name conflicts are not imported; the similar name is imported inactive and flagged.
        Assert.Equal(
            [("CS-CMP-001", "Complaint Handling"), ("REG-NOC-001", "NOC for Resale"), ("REG-NOC-002", "NOC for Golden Visa"),
             ("REG-NOC-003", "NOC for Mortgage"), ("HO-NOC-001", "NOC for Handover")],
            result.Rows.Where(r => r.ExistingNameConflict is not null).Select(r => (r.RequestCode, r.ExistingNameConflict!)));
        Assert.True(Row(result, "CS-CMP-001").Imported);
        Assert.False(Row(result, "REG-NOC-001").Imported);

        Assert.Equal(26, result.Rows.Count(r => r.Imported));
        Assert.Equal(
            ["FM-SVC-001", "BRK-COM-001", "BRK-CHK-001", "SAL-INQ-001", "LEG-INQ-001", "REC-HR-001", "REC-MKT-001"],
            result.Rows.Where(r => r.Imported && r.DraftBecauseOfUnresolvedDependency).Select(r => r.RequestCode));
    }

    [Fact]
    public async Task Created_request_types_are_inactive_and_carry_the_workbook_configuration()
    {
        await using var db = await CreateWithFacilitiesManagementAsync();
        var result = await NewRequestTypesImporter.ImportAsync(db, Now, createMissingOwningDepartments: true);

        foreach (var outcome in result.Rows.Where(r => r.Outcome is Outcome.Created or Outcome.CreatedWithDraftWorkflow))
        {
            var row = NewRequestTypesBusinessReview.Rows().Single(r => r.RequestCode == outcome.RequestCode);
            var requestType = await db.RequestTypes.SingleAsync(r => r.RequestTypeId == outcome.RequestTypeId);
            var workflow = await db.Workflows.SingleAsync(w => w.WorkflowId == requestType.WorkflowId);
            var department = await db.Departments.SingleAsync(d => d.DepartmentId == requestType.DepartmentId);

            Assert.False(requestType.IsActive);
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
            Assert.Equal(
                outcome.Outcome == Outcome.Created ? WorkflowVersionStatus.Published : WorkflowVersionStatus.Draft,
                version.Status);
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
    }

    [Fact]
    public async Task Existing_configuration_is_never_modified()
    {
        await using var db = await CreateWithFacilitiesManagementAsync();
        var requestTypesBefore = await Snapshot(db);
        var approvalsBefore = await db.RequestTypeApprovalRequirements.AsNoTracking()
            .Select(r => new { r.RequestTypeApprovalRequirementId, r.RequestTypeId, r.ApprovalType, r.TargetKind, r.TargetRoleName, r.TargetDepartmentId, r.BlocksWorkUntilApproved, r.IsActive })
            .ToListAsync();
        var departmentsBefore = await db.Departments.AsNoTracking().Select(d => new { d.DepartmentId, d.Name, d.Code, d.IsActive }).ToListAsync();
        var settingsBefore = await db.DepartmentWorkflowSettings.AsNoTracking().CountAsync();

        await NewRequestTypesImporter.ImportAsync(db, Now);

        var requestTypesAfter = await Snapshot(db);
        Assert.All(requestTypesBefore, before => Assert.Contains(before, requestTypesAfter));
        Assert.Equal(approvalsBefore, await db.RequestTypeApprovalRequirements.AsNoTracking()
            .Select(r => new { r.RequestTypeApprovalRequirementId, r.RequestTypeId, r.ApprovalType, r.TargetKind, r.TargetRoleName, r.TargetDepartmentId, r.BlocksWorkUntilApproved, r.IsActive })
            .ToListAsync());
        Assert.Equal(departmentsBefore, await db.Departments.AsNoTracking().Select(d => new { d.DepartmentId, d.Name, d.Code, d.IsActive }).ToListAsync());
        Assert.Equal(settingsBefore, await db.DepartmentWorkflowSettings.CountAsync());

        // The seeded NOC request types keep their own workflows and SLA rows.
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
    public async Task Running_twice_creates_nothing_the_second_time()
    {
        await using var db = await CreateWithFacilitiesManagementAsync();
        var first = await NewRequestTypesImporter.ImportAsync(db, Now, createMissingOwningDepartments: true);
        var counts = await Counts(db);

        var second = await NewRequestTypesImporter.ImportAsync(db, Now.AddDays(1), createMissingOwningDepartments: true);

        Assert.Equal(counts, await Counts(db));
        foreach (var row in first.Rows)
        {
            var again = Row(second, row.RequestCode);
            if (row.Outcome is Outcome.Created or Outcome.CreatedWithDraftWorkflow)
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
    public async Task An_imported_row_renamed_during_uat_is_still_recognised_by_its_request_code()
    {
        await using var db = await CreateWithFacilitiesManagementAsync();
        var first = await NewRequestTypesImporter.ImportAsync(db, Now);
        var id = Row(first, "CS-GEN-001").RequestTypeId;

        var imported = await db.RequestTypes.SingleAsync(r => r.RequestTypeId == id);
        imported.Update("General Customer Inquiry", imported.WorkflowId, imported.DefaultPriorityId, imported.AllowAgentPriorityChange,
            imported.AllowPendingCustomer, imported.AllowPendingInternal, imported.AllowReopen, imported.RequiredFieldsJson);
        imported.Activate();
        await db.SaveChangesAsync();
        var counts = await Counts(db);

        var second = await NewRequestTypesImporter.ImportAsync(db, Now);

        Assert.Equal(Outcome.AlreadyImported, Row(second, "CS-GEN-001").Outcome);
        Assert.Equal(counts, await Counts(db));
        var after = await db.RequestTypes.AsNoTracking().SingleAsync(r => r.RequestTypeId == id);
        Assert.Equal("General Customer Inquiry", after.Name);
        Assert.True(after.IsActive);
    }

    [Fact]
    public async Task A_same_named_request_type_created_outside_the_import_is_left_alone()
    {
        await using var db = await CreateWithFacilitiesManagementAsync();
        var cs = await db.Departments.SingleAsync(d => d.Code == WorkflowReferenceData.CustomerServiceCode);
        var standard = await db.Workflows.SingleAsync(w => w.Code == WorkflowReferenceData.StandardTemplateCode);
        db.RequestTypes.Add(new RequestType(cs.DepartmentId, "General Inquiry", standard.WorkflowId, (byte)PriorityLevel.High,
            allowAgentPriorityChange: true, allowPendingCustomer: false, allowPendingInternal: false, allowReopen: false));
        await db.SaveChangesAsync();

        var result = await NewRequestTypesImporter.ImportAsync(db, Now);

        Assert.Equal(Outcome.SkippedExistingRequestType, Row(result, "CS-GEN-001").Outcome);
        Assert.False(await db.Workflows.AnyAsync(w => w.Code == "CS-GEN-001"));
        var untouched = await db.RequestTypes.SingleAsync(r => r.DepartmentId == cs.DepartmentId && r.Name == "General Inquiry");
        Assert.Equal((byte)PriorityLevel.High, untouched.DefaultPriorityId);
        Assert.False(untouched.AllowReopen);
        Assert.True(untouched.IsActive);
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
            db.Departments.Add(new Department("Facilities Management", "FM"));
            await db.SaveChangesAsync();
            await WorkflowReferenceData.SeedAsync(db);

            var first = await NewRequestTypesImporter.ImportAsync(db, Now, createMissingOwningDepartments: true);
            Assert.Equal(24, first.Count(Outcome.Created));
            Assert.Equal(OwningDepartmentResolution.Created, first.OwningDepartments.Single(d => d.Code == "LCS").Resolution);
        }

        await using (var db = new TigerCsDbContext(options))
        {
            var counts = await Counts(db);
            var second = await NewRequestTypesImporter.ImportAsync(db, Now, createMissingOwningDepartments: true);
            Assert.Equal(31, second.Count(Outcome.AlreadyImported));
            Assert.All(second.OwningDepartments, d => Assert.Equal(OwningDepartmentResolution.Existing, d.Resolution));
            Assert.Equal(counts, await Counts(db));
        }
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
            version.AppendStep(step.Name, step.Kind, step.IsOptional);
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

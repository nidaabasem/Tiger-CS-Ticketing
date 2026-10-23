using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;

namespace TigerCS.Tests.WorkflowConfiguration.Services;

/// <summary>The dedicated Genesys "Leasing Inquiry" request type in LCS: created active with a Published workflow and Normal priority, idempotent, and never touching existing rows.</summary>
public class LeasingInquiryRequestTypeImporterTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Creates_an_active_LCS_request_type_with_a_published_workflow_and_Normal_priority_and_no_assignment_rule()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        await NewRequestTypesImporter.ImportAsync(db, Now);

        var result = await LeasingInquiryRequestTypeImporter.ImportAsync(db, Now);

        Assert.Equal(LeasingInquiryImportOutcome.Created, result.Outcome);
        var lcs = await db.Departments.SingleAsync(d => d.Code == "LCS");
        var type = await db.RequestTypes.SingleAsync(r => r.RequestTypeId == result.RequestTypeId);
        Assert.Equal("Leasing Inquiry", type.Name);
        Assert.Equal(lcs.DepartmentId, type.DepartmentId);
        Assert.True(type.IsActive);
        Assert.Equal((byte)PriorityLevel.Medium, type.DefaultPriorityId);   // "Normal" is the Medium tier
        var published = await db.WorkflowTemplates.SingleAsync(w => w.WorkflowId == type.WorkflowId && w.Status == WorkflowVersionStatus.Published);
        Assert.Equal(1, published.VersionNumber);
        Assert.True((await db.Workflows.SingleAsync(w => w.WorkflowId == type.WorkflowId)).IsActive);
        Assert.Empty(await db.RequestTypeAssignmentRules.Where(r => r.RequestTypeId == type.RequestTypeId).ToListAsync());
        Assert.NotNull(await db.Categories.SingleOrDefaultAsync(c => c.DepartmentId == lcs.DepartmentId && c.Name == "Leasing Inquiry"));
    }

    [Fact]
    public async Task A_second_run_creates_nothing()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        await NewRequestTypesImporter.ImportAsync(db, Now);
        var first = await LeasingInquiryRequestTypeImporter.ImportAsync(db, Now);
        var types = await db.RequestTypes.CountAsync();
        var workflows = await db.Workflows.CountAsync();
        var templates = await db.WorkflowTemplates.CountAsync();
        var categories = await db.Categories.CountAsync();

        var second = await LeasingInquiryRequestTypeImporter.ImportAsync(db, Now);

        Assert.Equal(LeasingInquiryImportOutcome.AlreadyImported, second.Outcome);
        Assert.Equal(first.RequestTypeId, second.RequestTypeId);
        Assert.Equal(types, await db.RequestTypes.CountAsync());
        Assert.Equal(workflows, await db.Workflows.CountAsync());
        Assert.Equal(templates, await db.WorkflowTemplates.CountAsync());
        Assert.Equal(categories, await db.Categories.CountAsync());
    }

    [Fact]
    public async Task An_LCS_request_type_of_that_name_created_in_Administration_is_reused_unmodified()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        await NewRequestTypesImporter.ImportAsync(db, Now);
        var lcs = await db.Departments.SingleAsync(d => d.Code == "LCS");
        var workflowId = (await db.RequestTypes.FirstAsync(r => r.DepartmentId == lcs.DepartmentId)).WorkflowId;
        var manual = new RequestType(lcs.DepartmentId, "Leasing Inquiry", workflowId, (byte)PriorityLevel.High, false, true, true, false, isActive: false);
        db.RequestTypes.Add(manual);
        await db.SaveChangesAsync();
        var types = await db.RequestTypes.CountAsync();

        var result = await LeasingInquiryRequestTypeImporter.ImportAsync(db, Now);

        Assert.Equal(LeasingInquiryImportOutcome.ExistingRequestTypeReused, result.Outcome);
        Assert.Equal(manual.RequestTypeId, result.RequestTypeId);
        Assert.Equal(types, await db.RequestTypes.CountAsync());
        var after = await db.RequestTypes.AsNoTracking().SingleAsync(r => r.RequestTypeId == manual.RequestTypeId);
        Assert.False(after.IsActive);
        Assert.Equal((byte)PriorityLevel.High, after.DefaultPriorityId);
        Assert.Null(await db.Workflows.FirstOrDefaultAsync(w => w.Code == LeasingInquiryRequestTypeImporter.WorkflowCode));
    }

    [Fact]
    public async Task Without_the_LCS_department_nothing_is_written()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        var departments = await db.Departments.CountAsync();
        var types = await db.RequestTypes.CountAsync();

        var result = await LeasingInquiryRequestTypeImporter.ImportAsync(db, Now);

        Assert.Equal(LeasingInquiryImportOutcome.DepartmentMissing, result.Outcome);
        Assert.Null(result.RequestTypeId);
        Assert.Equal(departments, await db.Departments.CountAsync());
        Assert.Equal(types, await db.RequestTypes.CountAsync());
    }

    [Fact]
    public async Task A_workflow_code_already_used_by_something_else_is_skipped_never_modified()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        await NewRequestTypesImporter.ImportAsync(db, Now);
        db.Workflows.Add(new Workflow(LeasingInquiryRequestTypeImporter.WorkflowCode, "Something else", null, Now));
        await db.SaveChangesAsync();
        var types = await db.RequestTypes.CountAsync();

        var result = await LeasingInquiryRequestTypeImporter.ImportAsync(db, Now);

        Assert.Equal(LeasingInquiryImportOutcome.SkippedCodeInUse, result.Outcome);
        Assert.Equal(types, await db.RequestTypes.CountAsync());
    }
}

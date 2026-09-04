using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.WorkflowConfiguration.Services;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Repositories;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Tests.WorkflowConfiguration.Services;

/// <summary>
/// The read side of the configuration layer, exercised through the real
/// repositories over the real seed — template selection, capability
/// resolution, and SLA lookup behave exactly as phase-2 enforcement will
/// consume them.
/// </summary>
public class WorkflowConfigurationQueryServiceTests
{
    private static WorkflowConfigurationQueryService CreateService(TigerCsDbContext db) =>
        new(
            new RequestTypeRepository(db),
            new WorkflowTemplateRepository(db),
            new RequestTypeSlaPolicyRepository(db));

    private static async Task<int> DepartmentIdAsync(TigerCsDbContext db, string code) =>
        (await db.Departments.SingleAsync(d => d.Code == code)).DepartmentId;

    private static async Task<RequestType> RequestTypeAsync(TigerCsDbContext db, string departmentCode, string name) =>
        await db.RequestTypes.SingleAsync(
            r => r.Name == name && r.DepartmentId == db.Departments.Single(d => d.Code == departmentCode).DepartmentId);

    [Fact]
    public async Task Lists_only_a_departments_own_active_request_types()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        var service = CreateService(db);

        var collectionsId = await DepartmentIdAsync(db, WorkflowReferenceData.CollectionsCode);
        var listed = await service.ListRequestTypesForDepartmentAsync(collectionsId);

        Assert.Equal(["E-mail", "Send Receipts", "Ticketing System"], listed.Select(r => r.Name).OrderBy(n => n));
        Assert.All(listed, r => Assert.Equal(collectionsId, r.DepartmentId));

        // A deactivated request type disappears from the selectable list.
        var sendReceipts = await RequestTypeAsync(db, WorkflowReferenceData.CollectionsCode, "Send Receipts");
        sendReceipts.Deactivate();
        await db.SaveChangesAsync();

        var relisted = await service.ListRequestTypesForDepartmentAsync(collectionsId);
        Assert.DoesNotContain(relisted, r => r.Name == "Send Receipts");
    }

    [Fact]
    public async Task Resolves_the_workflow_with_steps_and_effective_capabilities()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        var service = CreateService(db);

        var resale = await RequestTypeAsync(db, WorkflowReferenceData.CustomerServiceCode, "NOC for Resale");
        var workflow = await service.GetWorkflowAsync(resale.RequestTypeId);

        Assert.NotNull(workflow);
        Assert.Equal(WorkflowReferenceData.WithPendingTemplateCode, workflow.WorkflowTemplateCode);
        Assert.Equal(
            workflow.Steps.OrderBy(s => s.Sequence).Select(s => s.Sequence), workflow.Steps.Select(s => s.Sequence));

        // Template B allows both pending kinds; NOC for Resale only allows
        // Pending Customer — the intersection is what enforcement sees.
        Assert.True(workflow.Capabilities.CanGoPendingCustomer);
        Assert.False(workflow.Capabilities.CanGoPendingInternal);
        Assert.False(workflow.Capabilities.RequiresApproval);
        Assert.True(workflow.Capabilities.CanReopen);
        Assert.True(workflow.Capabilities.CanChangePriority);
    }

    [Fact]
    public async Task Approval_flows_surface_their_approval_stage()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        var service = CreateService(db);

        var sendReceipts = await RequestTypeAsync(db, WorkflowReferenceData.CollectionsCode, "Send Receipts");
        var workflow = await service.GetWorkflowAsync(sendReceipts.RequestTypeId);

        Assert.NotNull(workflow);
        Assert.True(workflow.Capabilities.RequiresApproval);
        Assert.Contains(workflow.Steps, s => s.Kind == WorkflowStepKind.WaitingForApproval);
    }

    [Fact]
    public async Task Unknown_or_inactive_request_types_resolve_to_null()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        var service = CreateService(db);

        Assert.Null(await service.GetWorkflowAsync(requestTypeId: 999_999));

        var email = await RequestTypeAsync(db, WorkflowReferenceData.CustomerServiceCode, "E-mail");
        email.Deactivate();
        await db.SaveChangesAsync();

        Assert.Null(await service.GetWorkflowAsync(email.RequestTypeId));
    }

    [Fact]
    public async Task Resolves_the_sla_row_for_the_exact_request_type_and_priority()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        var service = CreateService(db);

        var mortgage = await RequestTypeAsync(db, WorkflowReferenceData.CustomerServiceCode, "NOC for Mortgage");

        var normal = await service.ResolveSlaAsync(
            mortgage.RequestTypeId, (byte)WorkflowReferenceData.NormalUrgencyPriority);
        Assert.NotNull(normal);
        Assert.Equal(10, normal.ResolutionTargetValue);
        Assert.Equal(12, normal.ResolutionMaximumValue);
        Assert.Equal(SlaDurationUnit.Days, normal.Unit);

        var urgent = await service.ResolveSlaAsync(
            mortgage.RequestTypeId, (byte)WorkflowReferenceData.UrgentUrgencyPriority);
        Assert.NotNull(urgent);
        Assert.Equal(2, urgent.ResolutionTargetValue);
        Assert.Equal(4, urgent.ResolutionMaximumValue);
    }

    [Fact]
    public async Task Unconfigured_priority_pairs_resolve_to_null_not_an_invented_fallback()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        var service = CreateService(db);

        var mortgage = await RequestTypeAsync(db, WorkflowReferenceData.CustomerServiceCode, "NOC for Mortgage");

        // No Critical/Low rows exist for NOC for Mortgage — how those fall
        // back to the per-priority SlaPolicy defaults is a phase-4 decision,
        // so the honest answer today is "not configured".
        Assert.Null(await service.ResolveSlaAsync(
            mortgage.RequestTypeId, (byte)TigerCS.Domain.Modules.SlaAndEscalation.PriorityLevel.Critical));
        Assert.Null(await service.ResolveSlaAsync(
            mortgage.RequestTypeId, (byte)TigerCS.Domain.Modules.SlaAndEscalation.PriorityLevel.Low));
    }

    [Fact]
    public async Task Conditional_sla_triggers_reach_the_consumer_unchanged()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        var service = CreateService(db);

        var sendReceipts = await RequestTypeAsync(db, WorkflowReferenceData.CollectionsCode, "Send Receipts");
        var sla = await service.ResolveSlaAsync(
            sendReceipts.RequestTypeId, (byte)WorkflowReferenceData.NormalUrgencyPriority);

        Assert.NotNull(sla);
        Assert.Equal(SlaTriggerType.ApprovalReceived, sla.Trigger);
        Assert.Null(sla.PausesOnPendingCustomer);
        Assert.Null(sla.PausesOnPendingInternal);
    }
}

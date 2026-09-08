using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.Administration.Services;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Administration.Services;

/// <summary>Workflow versioning through the application service: create, draft, publish, new version, immutability, deletion rules.</summary>
public class AdminWorkflowAppServiceTests
{
    private static readonly Guid Admin = Guid.NewGuid();

    private sealed record Fixture(
        AdminWorkflowAppService Service,
        FakeWorkflowRepository Workflows,
        FakeWorkflowTemplateRepository Versions,
        FakeRequestTypeRepository RequestTypes,
        FakeAuditEntryWriter Audit);

    private static Fixture Create()
    {
        var workflows = new FakeWorkflowRepository();
        var versions = new FakeWorkflowTemplateRepository();
        var requestTypes = new FakeRequestTypeRepository();
        var departments = new FakeDepartmentRepository();
        var employees = new FakeEmployeeRepository();
        var audit = new FakeAuditEntryWriter();
        var service = new AdminWorkflowAppService(
            workflows, versions, requestTypes, departments, employees,
            new FakeWorkflowConfigurationUnitOfWork(), audit, TimeProvider.System);
        return new Fixture(service, workflows, versions, requestTypes, audit);
    }

    [Fact]
    public async Task Create_makes_a_workflow_with_a_valid_draft_v1_skeleton()
    {
        var f = Create();

        var result = await f.Service.CreateAsync(Admin, new CreateWorkflowRequestDto("Send Receipts Workflow", "Collections"));

        Assert.Equal(AdminOutcome.Success, result.Outcome);
        var version = Assert.Single(result.Value!.Versions);
        Assert.Equal(WorkflowVersionStatus.Draft, version.Status);
        Assert.Equal(1, version.VersionNumber);
        Assert.Equal(5, version.StepCount);

        var detail = await f.Service.GetVersionAsync(version.WorkflowTemplateId);
        Assert.True(detail!.CanPublish, string.Join(" ", detail.Validation.Select(i => i.Message)));
        Assert.Equal("SEND-RECEIPTS-WORKFLOW", (await f.Workflows.GetByIdAsync(result.Value.WorkflowId))!.Code);
    }

    [Fact]
    public async Task Draft_editing_add_edit_configure_reorder_remove()
    {
        var f = Create();
        var created = await f.Service.CreateAsync(Admin, new CreateWorkflowRequestDto("Send Receipts", null));
        var versionId = created.Value!.Versions[0].WorkflowTemplateId;

        var added = await f.Service.AddStepAsync(Admin, versionId,
            new SaveStepRequestDto("Accounting Approval", WorkflowStepKind.WaitingForApproval, false, ApprovalType.AccountingApproval));
        Assert.Equal(AdminOutcome.Success, added.Outcome);
        var approval = added.Value!.Steps.Single(s => s.Kind == WorkflowStepKind.WaitingForApproval);
        Assert.Equal(6, approval.Sequence);

        // Move it up until it sits after the queue step (position 3).
        WorkflowVersionDetailDto moved = added.Value;
        for (var i = 0; i < 3; i++)
        {
            moved = (await f.Service.MoveStepAsync(Admin, versionId, approval.WorkflowTemplateStepId, new MoveStepRequestDto(StepMoveDirection.Up))).Value!;
        }

        Assert.Equal(3, moved.Steps.Single(s => s.WorkflowTemplateStepId == approval.WorkflowTemplateStepId).Sequence);

        var work = moved.Steps.Single(s => s.Kind == WorkflowStepKind.InProgress);
        var queue = moved.Steps.Single(s => s.Kind == WorkflowStepKind.Assigned);
        var branched = await f.Service.SetTransitionAsync(Admin, versionId, approval.WorkflowTemplateStepId,
            new SetTransitionRequestDto(WorkflowStepOutcome.Rejected, queue.WorkflowTemplateStepId));
        Assert.Equal(AdminOutcome.Success, branched.Outcome);
        var branch = Assert.Single(branched.Value!.Steps.Single(s => s.WorkflowTemplateStepId == approval.WorkflowTemplateStepId).Transitions);
        Assert.Equal(queue.Sequence, branch.TargetSequence);

        var renamed = await f.Service.UpdateStepAsync(Admin, versionId, work.WorkflowTemplateStepId,
            new SaveStepRequestDto("Send Receipts", WorkflowStepKind.InProgress, false, null));
        Assert.Equal("Send Receipts", renamed.Value!.Steps.Single(s => s.WorkflowTemplateStepId == work.WorkflowTemplateStepId).Name);

        var removed = await f.Service.RemoveStepAsync(Admin, versionId, queue.WorkflowTemplateStepId);
        Assert.Equal(AdminOutcome.Success, removed.Outcome);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, removed.Value!.Steps.Select(s => s.Sequence));
        Assert.Empty(removed.Value.Steps.Single(s => s.WorkflowTemplateStepId == approval.WorkflowTemplateStepId).Transitions);

        var invalidStep = await f.Service.AddStepAsync(Admin, versionId, new SaveStepRequestDto(" ", WorkflowStepKind.InProgress, false, null));
        Assert.Equal(AdminOutcome.ValidationFailed, invalidStep.Outcome);
        var badKind = await f.Service.AddStepAsync(Admin, versionId, new SaveStepRequestDto("x", (WorkflowStepKind)77, false, null));
        Assert.Equal(AdminOutcome.ValidationFailed, badKind.Outcome);
        Assert.Contains(f.Audit.Entries, e => e.Action == "AdminAddWorkflowStep" && e.ActorEmployeeId == Admin);
    }

    [Fact]
    public async Task Invalid_draft_cannot_publish_and_reports_every_problem()
    {
        var f = Create();
        var created = await f.Service.CreateAsync(Admin, new CreateWorkflowRequestDto("Broken", null));
        var versionId = created.Value!.Versions[0].WorkflowTemplateId;
        var close = (await f.Service.GetVersionAsync(versionId))!.Steps.Single(s => s.Kind == WorkflowStepKind.Closed);
        await f.Service.RemoveStepAsync(Admin, versionId, close.WorkflowTemplateStepId);
        await f.Service.AddStepAsync(Admin, versionId, new SaveStepRequestDto("Approval", WorkflowStepKind.WaitingForApproval, false, null));

        var publish = await f.Service.PublishAsync(Admin, versionId);

        Assert.Equal(AdminOutcome.ValidationFailed, publish.Outcome);
        Assert.Contains(publish.Errors!, e => e.Contains("no Close step", StringComparison.Ordinal));
        Assert.Contains(publish.Errors!, e => e.Contains("has no approval type", StringComparison.Ordinal));
        Assert.Equal(WorkflowVersionStatus.Draft, (await f.Service.GetVersionAsync(versionId))!.Status);
    }

    [Fact]
    public async Task Publish_then_new_version_copies_and_publishing_v2_never_alters_v1()
    {
        var f = Create();
        var created = await f.Service.CreateAsync(Admin, new CreateWorkflowRequestDto("Send Receipts", null));
        var workflowId = created.Value!.WorkflowId;
        var v1Id = created.Value.Versions[0].WorkflowTemplateId;

        var published = await f.Service.PublishAsync(Admin, v1Id);
        Assert.Equal(AdminOutcome.Success, published.Outcome);
        Assert.Equal(WorkflowVersionStatus.Published, published.Value!.Status);
        Assert.False(published.Value.IsEditable);

        // Published versions are immutable through the service too.
        Assert.Equal(AdminOutcome.Conflict, (await f.Service.AddStepAsync(Admin, v1Id, new SaveStepRequestDto("Extra", WorkflowStepKind.InProgress, false, null))).Outcome);
        Assert.Equal(AdminOutcome.Conflict, (await f.Service.PublishAsync(Admin, v1Id)).Outcome);
        Assert.Equal(AdminOutcome.Conflict, (await f.Service.DeleteDraftAsync(Admin, v1Id)).Outcome);

        var v2 = await f.Service.CreateVersionAsync(Admin, workflowId);
        Assert.Equal(AdminOutcome.Success, v2.Outcome);
        Assert.Equal(2, v2.Value!.VersionNumber);
        Assert.Equal(WorkflowVersionStatus.Draft, v2.Value.Status);
        Assert.Equal(published.Value.Steps.Select(s => (s.Sequence, s.Name, s.Kind)), v2.Value.Steps.Select(s => (s.Sequence, s.Name, s.Kind)));

        // Only one Draft at a time.
        Assert.Equal(AdminOutcome.Conflict, (await f.Service.CreateVersionAsync(Admin, workflowId)).Outcome);

        var v2Id = v2.Value.WorkflowTemplateId;
        await f.Service.AddStepAsync(Admin, v2Id, new SaveStepRequestDto("Accounting Approval", WorkflowStepKind.WaitingForApproval, false, ApprovalType.AccountingApproval));
        var approval = (await f.Service.GetVersionAsync(v2Id))!.Steps.Single(s => s.Kind == WorkflowStepKind.WaitingForApproval);
        for (var i = 0; i < 3; i++)
        {
            await f.Service.MoveStepAsync(Admin, v2Id, approval.WorkflowTemplateStepId, new MoveStepRequestDto(StepMoveDirection.Up));
        }

        var v2Published = await f.Service.PublishAsync(Admin, v2Id);
        Assert.Equal(AdminOutcome.Success, v2Published.Outcome);

        var v1After = await f.Service.GetVersionAsync(v1Id);
        Assert.Equal(WorkflowVersionStatus.Historical, v1After!.Status);
        Assert.Equal(5, v1After.Steps.Count);
        Assert.DoesNotContain(v1After.Steps, s => s.Kind == WorkflowStepKind.WaitingForApproval);

        var detail = await f.Service.GetAsync(workflowId);
        Assert.Equal([2, 1], detail!.Versions.Select(v => v.VersionNumber));
        Assert.Equal(WorkflowVersionStatus.Published, detail.Versions[0].Status);
        Assert.Equal(WorkflowVersionStatus.Historical, detail.Versions[1].Status);
        Assert.True(v1After.Validation.Count == 0, "read-only versions carry no validation summary");
    }

    [Fact]
    public async Task Draft_is_deletable_only_when_unreferenced_and_not_the_last_used_version()
    {
        var f = Create();
        var created = await f.Service.CreateAsync(Admin, new CreateWorkflowRequestDto("Temp", null));
        var workflowId = created.Value!.WorkflowId;
        var v1Id = created.Value.Versions[0].WorkflowTemplateId;

        // A request type still points at the workflow whose only version is this draft.
        f.RequestTypes.Add(new RequestType(1, "Temp RT", workflowId, (byte)PriorityLevel.Medium, false, false, false, true));
        Assert.Equal(AdminOutcome.Conflict, (await f.Service.DeleteDraftAsync(Admin, v1Id)).Outcome);

        await f.Service.PublishAsync(Admin, v1Id);
        var v2 = await f.Service.CreateVersionAsync(Admin, workflowId);

        // A pinned ticket (simulated count) makes even a draft undeletable.
        f.Versions.PinnedTicketCounts[v2.Value!.WorkflowTemplateId] = 1;
        Assert.Equal(AdminOutcome.Conflict, (await f.Service.DeleteDraftAsync(Admin, v2.Value.WorkflowTemplateId)).Outcome);

        f.Versions.PinnedTicketCounts.Remove(v2.Value.WorkflowTemplateId);
        Assert.Equal(AdminOutcome.Success, (await f.Service.DeleteDraftAsync(Admin, v2.Value.WorkflowTemplateId)).Outcome);
        Assert.Null(await f.Service.GetVersionAsync(v2.Value.WorkflowTemplateId));
        Assert.Equal(AdminOutcome.NotFound, (await f.Service.DeleteDraftAsync(Admin, 999)).Outcome);
    }

    [Fact]
    public async Task Workflow_cannot_be_deactivated_while_active_request_types_use_it()
    {
        var f = Create();
        var created = await f.Service.CreateAsync(Admin, new CreateWorkflowRequestDto("Used", null));
        f.RequestTypes.Add(new RequestType(1, "Uses it", created.Value!.WorkflowId, (byte)PriorityLevel.Medium, false, false, false, true));

        var refused = await f.Service.SetActivationAsync(Admin, created.Value.WorkflowId, new SetActiveRequestDto(false));
        Assert.Equal(AdminOutcome.Conflict, refused.Outcome);

        var summary = Assert.Single(await f.Service.ListAsync(includeInactive: true));
        Assert.Equal(1, summary.RequestTypeCount);
        Assert.NotNull(AdminWorkflowAppService.Catalog().StepKinds.Single(k => k.Kind == WorkflowStepKind.WaitingForApproval));
    }

    [Fact]
    public async Task Historical_version_remains_queryable_with_its_original_steps()
    {
        var f = Create();
        var created = await f.Service.CreateAsync(Admin, new CreateWorkflowRequestDto("Keep", null));
        var v1Id = created.Value!.Versions[0].WorkflowTemplateId;
        await f.Service.PublishAsync(Admin, v1Id);
        var v2 = await f.Service.CreateVersionAsync(Admin, created.Value.WorkflowId);
        await f.Service.PublishAsync(Admin, v2.Value!.WorkflowTemplateId);
        f.Versions.PinnedTicketCounts[v1Id] = 126;

        var historical = await f.Service.GetVersionAsync(v1Id);
        var list = await f.Service.GetAsync(created.Value.WorkflowId);

        Assert.Equal(WorkflowVersionStatus.Historical, historical!.Status);
        Assert.Equal(126, historical.TicketCount);
        Assert.Equal(5, historical.Steps.Count);
        Assert.False(historical.CanDelete);
        Assert.Equal(126, list!.Versions.Single(v => v.VersionNumber == 1).TicketCount);
    }
}

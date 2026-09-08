using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.WorkflowConfiguration.Domain;

/// <summary>
/// The Administration / Workflow Designer phase's aggregate rules: a Draft
/// is editable, a Published/Historical version is immutable, "Create New
/// Version" copies structure, and publish validation refuses every
/// structurally unsound Draft with management-readable wording.
/// </summary>
public class WorkflowVersioningDomainTests
{
    private static readonly DateTime Now = new(2026, 9, 8, 10, 0, 0, DateTimeKind.Utc);

    private static WorkflowTemplate EmptyDraft(int versionNumber = 1) =>
        new(workflowId: 7, versionNumber, versionNumber == 1 ? "SR" : $"SR-V{versionNumber}", "Send Receipts", null,
            allowsPendingCustomer: false, allowsPendingInternal: false, requiresApproval: false, Now, createdByEmployeeId: null);

    /// <summary>Gives every step a distinct id the way EF would after a save, so id-based editing works in memory.</summary>
    private static void AssignIds(WorkflowTemplate version)
    {
        var id = 1;
        foreach (var step in version.Steps)
        {
            typeof(WorkflowTemplateStep).GetProperty(nameof(WorkflowTemplateStep.WorkflowTemplateStepId))!.SetValue(step, id++);
        }
    }

    private static WorkflowTemplate ValidDraft()
    {
        var draft = EmptyDraft();
        draft.AppendStep("Ticket Created", WorkflowStepKind.Created);
        draft.AppendStep("Collections Queue", WorkflowStepKind.Assigned);
        draft.AppendStep("Accounting Approval", WorkflowStepKind.WaitingForApproval, approvalType: ApprovalType.AccountingApproval);
        draft.AppendStep("Send Receipts", WorkflowStepKind.InProgress);
        draft.AppendStep("Resolve", WorkflowStepKind.Resolved);
        draft.AppendStep("Close", WorkflowStepKind.Closed);
        AssignIds(draft);
        return draft;
    }

    // ---- Draft editing ------------------------------------------------------

    [Fact]
    public void AppendStep_numbers_steps_contiguously_and_UpdateStep_edits_in_place()
    {
        var draft = ValidDraft();

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, draft.Steps.Select(s => s.Sequence));

        draft.UpdateStep(4, "Send the receipts", WorkflowStepKind.InProgress, isOptional: false, approvalType: null);

        Assert.Equal("Send the receipts", draft.Steps[3].Name);
    }

    [Fact]
    public void RemoveStep_renumbers_and_drops_branches_pointing_at_it()
    {
        var draft = ValidDraft();
        draft.SetStepTransition(3, WorkflowStepOutcome.Rejected, targetWorkflowTemplateStepId: 2);

        draft.RemoveStep(2);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, draft.Steps.Select(s => s.Sequence));
        Assert.Empty(draft.GetStep(3).Transitions);
    }

    [Fact]
    public void MoveStepUp_and_Down_swap_positions_and_keep_branches_attached_to_steps()
    {
        var draft = ValidDraft();
        draft.SetStepTransition(3, WorkflowStepOutcome.Approved, targetWorkflowTemplateStepId: 4);

        draft.MoveStepDown(3);

        var approval = draft.GetStep(3);
        Assert.Equal(4, approval.Sequence);
        Assert.Equal(3, draft.GetStep(4).Sequence);
        // The branch still points at the "Send Receipts" step, wherever it now sits.
        Assert.Same(draft.GetStep(4), Assert.Single(approval.Transitions).TargetStep);

        draft.MoveStepUp(3);
        Assert.Equal(3, draft.GetStep(3).Sequence);

        // Edge no-ops.
        draft.MoveStepUp(1);
        draft.MoveStepDown(6);
        Assert.Equal(1, draft.GetStep(1).Sequence);
        Assert.Equal(6, draft.GetStep(6).Sequence);
    }

    [Fact]
    public void Outcome_branches_only_exist_on_approval_steps_and_approval_type_only_on_them()
    {
        var draft = ValidDraft();

        Assert.Throws<WorkflowStepConfigurationException>(() => draft.SetStepTransition(4, WorkflowStepOutcome.Approved, 5));
        Assert.Throws<WorkflowStepConfigurationException>(() =>
            draft.UpdateStep(4, "Work", WorkflowStepKind.InProgress, false, ApprovalType.AccountingApproval));

        // Changing an approval step to another kind drops its branches.
        draft.SetStepTransition(3, WorkflowStepOutcome.Rejected, 2);
        draft.UpdateStep(3, "Review", WorkflowStepKind.Review, false, null);
        Assert.Empty(draft.GetStep(3).Transitions);
    }

    [Fact]
    public void Unknown_step_ids_are_reported_not_ignored()
    {
        var draft = ValidDraft();

        Assert.Throws<WorkflowStepNotFoundException>(() => draft.RemoveStep(99));
        Assert.Throws<WorkflowStepNotFoundException>(() => draft.SetStepTransition(3, WorkflowStepOutcome.Approved, 99));
    }

    // ---- Publication and immutability ----------------------------------------

    [Fact]
    public void Publish_makes_the_version_read_only_and_widens_flags_to_match_steps()
    {
        var draft = ValidDraft();
        var publisher = Guid.NewGuid();

        draft.Publish(Now, publisher);

        Assert.True(draft.IsPublished);
        Assert.Equal(Now, draft.PublishedAtUtc);
        Assert.Equal(publisher, draft.PublishedByEmployeeId);
        Assert.True(draft.RequiresApproval, "an approval step implies the approval capability");

        Assert.Throws<WorkflowVersionNotEditableException>(() => draft.AppendStep("Extra", WorkflowStepKind.InProgress));
        Assert.Throws<WorkflowVersionNotEditableException>(() => draft.UpdateStep(4, "X", WorkflowStepKind.InProgress, false, null));
        Assert.Throws<WorkflowVersionNotEditableException>(() => draft.RemoveStep(4));
        Assert.Throws<WorkflowVersionNotEditableException>(() => draft.MoveStepUp(4));
        Assert.Throws<WorkflowVersionNotEditableException>(() => draft.SetStepTransition(3, WorkflowStepOutcome.Approved, 4));
        Assert.Throws<WorkflowVersionNotEditableException>(() => draft.UpdateSettings("Renamed", null, true, true, true));
        Assert.Throws<WorkflowVersionNotEditableException>(() => draft.Publish(Now, publisher));
    }

    [Fact]
    public void Historical_versions_stay_read_only_and_only_a_published_version_becomes_historical()
    {
        var draft = ValidDraft();
        Assert.Throws<InvalidOperationException>(draft.MarkHistorical);

        draft.Publish(Now, null);
        draft.MarkHistorical();

        Assert.True(draft.IsHistorical);
        Assert.Throws<WorkflowVersionNotEditableException>(() => draft.AppendStep("Extra", WorkflowStepKind.InProgress));
        Assert.Equal(6, draft.Steps.Count);
    }

    [Fact]
    public void CopyStepsFrom_reproduces_steps_and_branches_independently()
    {
        var v1 = ValidDraft();
        v1.SetStepTransition(3, WorkflowStepOutcome.Rejected, 2);
        v1.Publish(Now, null);

        var v2 = EmptyDraft(2);
        v2.CopyStepsFrom(v1);
        AssignIds(v2);

        Assert.Equal(v1.Steps.Select(s => (s.Sequence, s.Name, s.Kind, s.ApprovalType)), v2.Steps.Select(s => (s.Sequence, s.Name, s.Kind, s.ApprovalType)));
        var copiedBranch = Assert.Single(v2.GetStep(3).Transitions);
        Assert.Same(v2.GetStep(2), copiedBranch.TargetStep);

        // Editing the copy never touches the published source.
        v2.RemoveStep(2);
        Assert.Equal(6, v1.Steps.Count);
        Assert.Equal(5, v2.Steps.Count);
        Assert.Throws<InvalidOperationException>(() => v2.CopyStepsFrom(v1));
    }

    [Fact]
    public void PublishAsSeededBaseline_skips_validation_for_pre_versioning_templates_only()
    {
        // The phase-1 "With Approval" pattern's approval step deliberately
        // has no approval type; the seed still needs it Published.
        var legacy = EmptyDraft();
        legacy.AppendStep("Ticket Created", WorkflowStepKind.Created);
        legacy.AppendStep("Waiting for Approval", WorkflowStepKind.WaitingForApproval);
        legacy.AppendStep("Resolved", WorkflowStepKind.Resolved);
        legacy.AppendStep("Closed", WorkflowStepKind.Closed);

        Assert.Throws<WorkflowVersionInvalidException>(() => legacy.Publish(Now, null));

        legacy.PublishAsSeededBaseline(Now);
        Assert.True(legacy.IsPublished);
        Assert.Null(legacy.PublishedByEmployeeId);
    }

    // ---- Publish validation -----------------------------------------------------

    private static IReadOnlyList<string> Errors(WorkflowTemplate draft) =>
        draft.Validate().Where(i => i.Severity == WorkflowValidationSeverity.Error).Select(i => i.Message).ToList();

    [Fact]
    public void Empty_workflow_cannot_publish()
    {
        var draft = EmptyDraft();

        var errors = Errors(draft);

        Assert.Contains(errors, e => e.Contains("no steps", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<WorkflowVersionInvalidException>(() => draft.Publish(Now, null));
    }

    [Fact]
    public void Missing_or_misplaced_start_step_is_reported()
    {
        var noStart = EmptyDraft();
        noStart.AppendStep("Work", WorkflowStepKind.InProgress);
        noStart.AppendStep("Resolve", WorkflowStepKind.Resolved);
        noStart.AppendStep("Close", WorkflowStepKind.Closed);
        Assert.Contains(Errors(noStart), e => e.Contains("no Start step", StringComparison.Ordinal));

        var lateStart = EmptyDraft();
        lateStart.AppendStep("Work", WorkflowStepKind.InProgress);
        lateStart.AppendStep("Ticket Created", WorkflowStepKind.Created);
        lateStart.AppendStep("Resolve", WorkflowStepKind.Resolved);
        lateStart.AppendStep("Close", WorkflowStepKind.Closed);
        Assert.Contains(Errors(lateStart), e => e.Contains("first step must be the Start step", StringComparison.Ordinal));
    }

    [Fact]
    public void Terminal_path_requires_resolve_before_a_final_close_step()
    {
        var noClose = EmptyDraft();
        noClose.AppendStep("Ticket Created", WorkflowStepKind.Created);
        noClose.AppendStep("Resolve", WorkflowStepKind.Resolved);
        Assert.Contains(Errors(noClose), e => e.Contains("no Close step", StringComparison.Ordinal));

        var closeNotLast = EmptyDraft();
        closeNotLast.AppendStep("Ticket Created", WorkflowStepKind.Created);
        closeNotLast.AppendStep("Resolve", WorkflowStepKind.Resolved);
        closeNotLast.AppendStep("Close", WorkflowStepKind.Closed);
        closeNotLast.AppendStep("Work", WorkflowStepKind.InProgress);
        Assert.Contains(Errors(closeNotLast), e => e.Contains("Close step must be the last step", StringComparison.Ordinal));

        var noResolve = EmptyDraft();
        noResolve.AppendStep("Ticket Created", WorkflowStepKind.Created);
        noResolve.AppendStep("Close", WorkflowStepKind.Closed);
        Assert.Contains(Errors(noResolve), e => e.Contains("no Resolve step", StringComparison.Ordinal));
    }

    [Fact]
    public void Approval_step_without_a_controlled_approval_type_cannot_publish()
    {
        var draft = EmptyDraft();
        draft.AppendStep("Ticket Created", WorkflowStepKind.Created);
        draft.AppendStep("Approval", WorkflowStepKind.WaitingForApproval);
        draft.AppendStep("Resolve", WorkflowStepKind.Resolved);
        draft.AppendStep("Close", WorkflowStepKind.Closed);

        var errors = Errors(draft);

        Assert.Contains(errors, e => e.Contains("has no approval type", StringComparison.Ordinal));
        Assert.Throws<WorkflowVersionInvalidException>(() => draft.Publish(Now, null));
        Assert.Throws<ArgumentException>(() => draft.AppendStep("Bad", WorkflowStepKind.WaitingForApproval, approvalType: (ApprovalType)99));
    }

    [Fact]
    public void Branch_to_a_missing_or_self_step_and_unreachable_steps_are_reported()
    {
        var draft = ValidDraft();
        draft.SetStepTransition(3, WorkflowStepOutcome.Approved, 5); // Approved skips "Send Receipts" → step 4 unreachable
        Assert.Contains(Errors(draft), e => e.Contains("'Send Receipts' can never be reached", StringComparison.Ordinal));

        var selfLoop = ValidDraft();
        var approval = selfLoop.GetStep(3);
        typeof(WorkflowTemplateStep)
            .GetMethod("SetTransition", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(approval, [WorkflowStepOutcome.Approved, approval]);
        Assert.Contains(Errors(selfLoop), e => e.Contains("cannot point back at the same step", StringComparison.Ordinal));
    }

    [Fact]
    public void A_valid_draft_publishes_with_only_non_blocking_notes()
    {
        var draft = ValidDraft();

        var issues = draft.Validate();

        Assert.DoesNotContain(issues, i => i.Severity == WorkflowValidationSeverity.Error);
        Assert.Contains(issues, i => i.Severity == WorkflowValidationSeverity.Warning && i.Message.Contains("no Rejected branch", StringComparison.Ordinal));
        draft.Publish(Now, null);
        Assert.True(draft.IsPublished);
    }

    [Fact]
    public void Step_kind_catalog_offers_only_the_controlled_types_and_names_them_for_management()
    {
        Assert.Equal(Enum.GetValues<WorkflowStepKind>().Length, WorkflowStepKinds.All.Count);
        Assert.All(Enum.GetValues<WorkflowStepKind>(), kind => Assert.True(WorkflowStepKinds.IsSupported(kind)));

        var approval = WorkflowStepKinds.Describe(WorkflowStepKind.WaitingForApproval);
        Assert.True(approval.RequiresApprovalType);
        Assert.True(approval.SupportsOutcomeBranches);
        Assert.Equal("Approval", approval.Label);
        Assert.True(WorkflowStepKinds.Describe(WorkflowStepKind.Created).IsStart);
        Assert.True(WorkflowStepKinds.Describe(WorkflowStepKind.Closed).IsTerminal);
    }

    [Fact]
    public void Workflow_codes_derive_from_names_and_stay_within_the_column()
    {
        Assert.Equal("SEND-RECEIPTS-WORKFLOW", Workflow.CodeFromName("Send Receipts Workflow"));
        Assert.Equal("WORKFLOW", Workflow.CodeFromName("!!!"));
        Assert.True(Workflow.CodeFromName(new string('x', 80)).Length <= Workflow.CodeMaxLength);
        Assert.Throws<ArgumentException>(() => new Workflow(new string('A', Workflow.CodeMaxLength + 1), "n", null, Now));
    }

    [Fact]
    public void Test_builders_produce_publishable_baseline_shapes()
    {
        Assert.True(TestWorkflows.PublishedPending(1).IsPublished);
        Assert.True(TestWorkflows.PublishedStandard(2).IsPublished);
    }
}

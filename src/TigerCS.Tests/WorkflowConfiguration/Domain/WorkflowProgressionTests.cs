using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Tests.WorkflowConfiguration.Domain;

/// <summary>The pure progression and readiness rules behind configuration enforcement.</summary>
public class WorkflowProgressionTests
{
    private const int Intake = 1, Handover = 2, Facilities = 3, Collections = 4;
    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

    private static WorkflowTemplate Version(Action<WorkflowTemplate>? extra = null)
    {
        var version = new WorkflowTemplate(1, 1, "T-V1", "T", null, true, false, false, Now, null);
        version.AppendStep("Ticket Created", WorkflowStepKind.Created);
        version.AppendStep("Intake Queue", WorkflowStepKind.Assigned, departmentId: Intake);
        version.AppendStep("Intake Agent", WorkflowStepKind.InProgress);
        version.AppendStep("Waiting on customer", WorkflowStepKind.PendingCustomer, isOptional: true);
        version.AppendStep("Facilities (if needed)", WorkflowStepKind.Assigned, isOptional: true, departmentId: Facilities);
        version.AppendStep("Handoff to Handover", WorkflowStepKind.Assigned, departmentId: Handover);
        version.AppendStep("Handover Work", WorkflowStepKind.InProgress);
        version.AppendStep("Return to Intake", WorkflowStepKind.Assigned, departmentId: Intake);
        version.AppendStep("Intake Follow-up", WorkflowStepKind.InProgress);
        extra?.Invoke(version);
        version.AppendStep("Resolve", WorkflowStepKind.Resolved);
        version.AppendStep("Close", WorkflowStepKind.Closed);
        return version;
    }

    private static WorkflowTemplateStep Step(WorkflowTemplate version, string name) => version.Steps.Single(s => s.Name == name);

    [Fact]
    public void A_ticket_enters_at_its_departments_queue_and_assignment_moves_it_to_the_work_step()
    {
        var version = Version();

        var entry = WorkflowProgression.Entry(version, Intake);

        Assert.Equal("Intake Queue", entry!.Name);
        Assert.Null(WorkflowProgression.Entry(version, Collections));
        Assert.Equal("Intake Agent", WorkflowProgression.AfterOwnerAssigned(version, entry)!.Name);
        Assert.Null(WorkflowProgression.AfterOwnerAssigned(version, Step(version, "Intake Agent")));
    }

    [Fact]
    public void A_transfer_must_target_the_next_queue_step_optional_steps_and_pending_markers_may_be_skipped()
    {
        var version = Version();
        var agent = Step(version, "Intake Agent");

        Assert.Equal("Facilities (if needed)", WorkflowProgression.ForTransfer(version, agent, Facilities)!.Name);
        Assert.Equal("Handoff to Handover", WorkflowProgression.ForTransfer(version, agent, Handover)!.Name);
        Assert.Null(WorkflowProgression.ForTransfer(version, agent, Collections));
        Assert.Null(WorkflowProgression.ForTransfer(version, agent, Intake)); // the return comes after Handover's work
        // Pending Customer markers are not positions, so they are never "expected next".
        Assert.Equal("Facilities (if needed) (optional) or Handoff to Handover", WorkflowProgression.DescribeExpectedNext(version, agent));
    }

    [Fact]
    public void Resolve_must_be_next_unless_the_outcome_ends_the_request_and_Close_must_follow_Resolve()
    {
        var version = Version();

        Assert.Null(WorkflowProgression.ForResolve(version, Step(version, "Intake Agent"), completesWork: true));
        Assert.Equal("Resolve", WorkflowProgression.ForResolve(version, Step(version, "Intake Agent"), completesWork: false)!.Name);
        Assert.Equal("Resolve", WorkflowProgression.ForResolve(version, Step(version, "Intake Follow-up"), completesWork: true)!.Name);
        Assert.Null(WorkflowProgression.ForClose(version, Step(version, "Intake Follow-up")));
        Assert.Equal("Close", WorkflowProgression.ForClose(version, Step(version, "Resolve"))!.Name);
    }

    [Fact]
    public void Reopen_lands_on_the_last_queue_step_of_the_target_department()
    {
        var version = Version();

        Assert.Equal("Return to Intake", WorkflowProgression.ForReopen(version, Intake)!.Name);
        Assert.Equal("Handoff to Handover", WorkflowProgression.ForReopen(version, Handover)!.Name);
        Assert.Null(WorkflowProgression.ForReopen(version, Collections));
    }

    [Fact]
    public void A_version_is_trackable_only_with_supported_kinds_named_departments_and_its_own_entry_queue()
    {
        Assert.Empty(ConfiguredRuntimeReadiness.EvaluateVersion(Version(), Intake, _ => true));

        var withApproval = Version(v => v.AppendStep("Approval", WorkflowStepKind.WaitingForApproval, approvalType: ApprovalType.AccountingApproval));
        Assert.Contains(ConfiguredRuntimeReadiness.EvaluateVersion(withApproval, Intake, _ => true), i => i.Contains("cannot track"));

        var unnamed = Version(v => v.AppendStep("Handoff to Legal", WorkflowStepKind.Assigned));
        Assert.Contains(ConfiguredRuntimeReadiness.EvaluateVersion(unnamed, Intake, _ => true), i => i.Contains("does not name its department"));

        var mandatoryPending = Version(v => v.AppendStep("Waiting", WorkflowStepKind.PendingCustomer));
        Assert.Contains(ConfiguredRuntimeReadiness.EvaluateVersion(mandatoryPending, Intake, _ => true), i => i.Contains("must be optional"));

        Assert.Contains(ConfiguredRuntimeReadiness.EvaluateVersion(Version(), Collections, _ => true), i => i.Contains("own department"));
        Assert.Contains(ConfiguredRuntimeReadiness.EvaluateVersion(Version(), Intake, id => id != Handover), i => i.Contains("inactive"));
    }

    [Fact]
    public void An_SLA_row_applies_at_runtime_only_as_a_single_ticket_created_duration_with_a_decided_basis()
    {
        RequestTypeSlaPolicy Row(SlaTriggerType trigger = SlaTriggerType.TicketCreated, int? max = null, SlaClockBasis? basis = SlaClockBasis.BusinessHours, bool immediate = false) =>
            new(1, (byte)PriorityLevel.Medium, trigger, SlaDurationUnit.Hours, 4, null, immediate ? null : 12, immediate ? null : max,
                isImmediate: immediate, clockBasis: basis, firstResponseUnit: SlaDurationUnit.Hours);

        Assert.Empty(ConfiguredRuntimeReadiness.SlaRowIssues(Row()));
        Assert.Single(ConfiguredRuntimeReadiness.SlaRowIssues(Row(trigger: SlaTriggerType.ApprovalReceived)));
        Assert.Single(ConfiguredRuntimeReadiness.SlaRowIssues(Row(max: 14)));
        Assert.Single(ConfiguredRuntimeReadiness.SlaRowIssues(Row(basis: null)));
        Assert.Single(ConfiguredRuntimeReadiness.SlaRowIssues(Row(immediate: true)));
    }

    [Theory]
    [InlineData(4, SlaDurationUnit.Hours, SlaClockBasis.BusinessHours, 240)]
    [InlineData(2, SlaDurationUnit.Days, SlaClockBasis.TwentyFourSeven, 2880)]
    [InlineData(30, SlaDurationUnit.Minutes, SlaClockBasis.BusinessHours, 30)]
    public void Configured_durations_convert_without_assuming_any_calendar(int value, SlaDurationUnit unit, SlaClockBasis basis, int minutes) =>
        Assert.Equal(minutes, ConfiguredRuntimeReadiness.ToMinutes(value, unit, basis));

    [Fact]
    public void A_business_day_is_never_converted_its_meaning_is_an_open_decision()
    {
        Assert.Throws<InvalidOperationException>(() => ConfiguredRuntimeReadiness.ToMinutes(1, SlaDurationUnit.Days, SlaClockBasis.BusinessHours));

        var businessDays = new RequestTypeSlaPolicy(1, (byte)PriorityLevel.Medium, SlaTriggerType.TicketCreated, SlaDurationUnit.Days,
            4, null, 1, null, clockBasis: SlaClockBasis.BusinessHours, firstResponseUnit: SlaDurationUnit.Hours);
        Assert.Contains(ConfiguredRuntimeReadiness.SlaRowIssues(businessDays), i => i.Contains("business day"));

        var calendarDays = new RequestTypeSlaPolicy(1, (byte)PriorityLevel.Medium, SlaTriggerType.TicketCreated, SlaDurationUnit.Days,
            4, null, 1, null, clockBasis: SlaClockBasis.TwentyFourSeven, firstResponseUnit: SlaDurationUnit.Hours);
        Assert.Empty(ConfiguredRuntimeReadiness.SlaRowIssues(calendarDays));
    }
}

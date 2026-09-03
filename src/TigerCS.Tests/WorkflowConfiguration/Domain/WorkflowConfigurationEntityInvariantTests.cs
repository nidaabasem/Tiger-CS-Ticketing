using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Tests.WorkflowConfiguration.Domain;

/// <summary>
/// Constructor/aggregate invariants of the Workflow/SLA Configuration
/// phase-1 entities — the rules that keep configuration data honest before
/// any database constraint sees it.
/// </summary>
public class WorkflowConfigurationEntityInvariantTests
{
    private static RequestType SomeRequestType(
        bool allowPendingCustomer = true, bool allowPendingInternal = true, bool allowReopen = true) =>
        new(
            departmentId: 1,
            name: "NOC for Resale",
            workflowTemplateId: 0,
            defaultPriorityId: (byte)PriorityLevel.Medium,
            allowAgentPriorityChange: true,
            allowPendingCustomer: allowPendingCustomer,
            allowPendingInternal: allowPendingInternal,
            allowReopen: allowReopen);

    [Fact]
    public void RequestType_requires_name()
    {
        var exception = Assert.Throws<ArgumentException>(() => new RequestType(
            1, "  ", 1, (byte)PriorityLevel.Medium, false, false, false, true));
        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void RequestType_rejects_priority_outside_the_fixed_set()
    {
        var exception = Assert.Throws<ArgumentException>(() => new RequestType(
            1, "NOC for Resale", 1, defaultPriorityId: 9, false, false, false, true));
        Assert.Equal("defaultPriorityId", exception.ParamName);
    }

    [Fact]
    public void WorkflowTemplate_requires_code_and_name()
    {
        Assert.Throws<ArgumentException>(() => new WorkflowTemplate(" ", "Standard", null, false, false, false));
        Assert.Throws<ArgumentException>(() => new WorkflowTemplate("STANDARD", " ", null, false, false, false));
    }

    [Fact]
    public void WorkflowTemplate_steps_must_have_strictly_increasing_sequences()
    {
        var template = new WorkflowTemplate("T", "Template", null, true, true, false);
        template.AddStep(1, "Ticket Created", WorkflowStepKind.Created);
        template.AddStep(3, "In Progress", WorkflowStepKind.InProgress);

        Assert.Throws<ArgumentException>(() => template.AddStep(3, "Resolved", WorkflowStepKind.Resolved));
        Assert.Throws<ArgumentException>(() => template.AddStep(2, "Resolved", WorkflowStepKind.Resolved));

        template.AddStep(4, "Resolved", WorkflowStepKind.Resolved);
        Assert.Equal(new byte[] { 1, 3, 4 }, template.Steps.Select(s => s.Sequence));
    }

    [Fact]
    public void Capabilities_are_the_intersection_of_template_and_request_type()
    {
        // Template allows both pending kinds; the request type switches off
        // pending internal — the narrower answer must win in both directions.
        var template = new WorkflowTemplate("PENDING", "Request With Pending", null,
            allowsPendingCustomer: true, allowsPendingInternal: true, requiresApproval: false);
        var requestType = SomeRequestType(allowPendingCustomer: true, allowPendingInternal: false);

        var capabilities = WorkflowCapabilities.Resolve(template, requestType);

        Assert.True(capabilities.CanGoPendingCustomer);
        Assert.False(capabilities.CanGoPendingInternal);
        Assert.False(capabilities.RequiresApproval);
        Assert.True(capabilities.CanReopen);
        Assert.True(capabilities.CanChangePriority);
    }

    [Fact]
    public void Capabilities_request_type_cannot_widen_a_forbidding_template()
    {
        var standard = new WorkflowTemplate("STANDARD", "Standard Request", null,
            allowsPendingCustomer: false, allowsPendingInternal: false, requiresApproval: false);
        var requestType = SomeRequestType(allowPendingCustomer: true, allowPendingInternal: true);

        var capabilities = WorkflowCapabilities.Resolve(standard, requestType);

        Assert.False(capabilities.CanGoPendingCustomer);
        Assert.False(capabilities.CanGoPendingInternal);
    }

    [Fact]
    public void Sla_range_upper_bound_cannot_precede_lower()
    {
        var exception = Assert.Throws<ArgumentException>(() => new RequestTypeSlaPolicy(
            1, (byte)PriorityLevel.Medium, SlaTriggerType.TicketCreated, SlaDurationUnit.Days,
            firstResponseTargetValue: null, firstResponseMaximumValue: null,
            resolutionTargetValue: 12, resolutionMaximumValue: 10));
        Assert.Equal("maximumValue", exception.ParamName);
    }

    [Fact]
    public void Sla_range_keeps_both_bounds_verbatim()
    {
        // "10–12 Days" stays 10 and 12 Days — never collapsed to one number.
        var policy = new RequestTypeSlaPolicy(
            1, (byte)PriorityLevel.Medium, SlaTriggerType.TicketCreated, SlaDurationUnit.Days,
            null, null, resolutionTargetValue: 10, resolutionMaximumValue: 12);

        Assert.Equal(10, policy.ResolutionTargetValue);
        Assert.Equal(12, policy.ResolutionMaximumValue);
        Assert.Equal(SlaDurationUnit.Days, policy.Unit);
    }

    [Fact]
    public void Sla_immediate_and_resolution_values_are_mutually_exclusive()
    {
        Assert.Throws<ArgumentException>(() => new RequestTypeSlaPolicy(
            1, (byte)PriorityLevel.High, SlaTriggerType.TicketCreated, SlaDurationUnit.Days,
            null, null, resolutionTargetValue: 1, resolutionMaximumValue: null, isImmediate: true));

        var immediate = new RequestTypeSlaPolicy(
            1, (byte)PriorityLevel.High, SlaTriggerType.TicketCreated, SlaDurationUnit.Days,
            null, null, null, null, isImmediate: true);
        Assert.True(immediate.IsImmediate);
        Assert.Null(immediate.ResolutionTargetValue);
    }

    [Fact]
    public void Sla_non_immediate_row_needs_a_resolution_bound()
    {
        Assert.Throws<ArgumentException>(() => new RequestTypeSlaPolicy(
            1, (byte)PriorityLevel.Medium, SlaTriggerType.TicketCreated, SlaDurationUnit.Days,
            null, null, resolutionTargetValue: null, resolutionMaximumValue: null));
    }

    [Fact]
    public void Sla_pause_flags_default_to_undecided_not_false()
    {
        // Whether Pending pauses the clock is an explicitly pending business
        // decision — the tri-state null is the honest default, not false.
        var policy = new RequestTypeSlaPolicy(
            1, (byte)PriorityLevel.Medium, SlaTriggerType.TicketCreated, SlaDurationUnit.Days,
            null, null, 1, 3);

        Assert.Null(policy.PausesOnPendingCustomer);
        Assert.Null(policy.PausesOnPendingInternal);
        Assert.Null(policy.ClockBasis);
    }

    [Fact]
    public void DepartmentWorkflowSettings_head_role_must_be_a_fixed_role()
    {
        Assert.Throws<ArgumentException>(() => new DepartmentWorkflowSettings(1, true, true, true, "Some Invented Role"));

        var defaulted = new DepartmentWorkflowSettings(1, true, true, true);
        Assert.Equal(Roles.DepartmentHead, defaulted.HeadRoleName);

        var supervisor = new DepartmentWorkflowSettings(1, true, true, true, Roles.CsSupervisor);
        Assert.Equal(Roles.CsSupervisor, supervisor.HeadRoleName);
    }
}

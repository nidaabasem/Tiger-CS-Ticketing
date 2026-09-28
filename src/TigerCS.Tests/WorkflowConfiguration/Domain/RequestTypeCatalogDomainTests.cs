using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Tests.WorkflowConfiguration.Domain;

/// <summary>The domain invariants the request-type catalog import relies on.</summary>
public class RequestTypeCatalogDomainTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

    private static RequestType NewRequestType() =>
        new(departmentId: 1, "General Inquiry", workflowId: 1, (byte)PriorityLevel.Medium, false, false, false, true);

    private static WorkflowTemplate NewDraft() =>
        new(workflowId: 1, versionNumber: 1, "T-V1", "T", null, false, false, false, Now, null);

    /// <summary>Gives unsaved steps the distinct ids the database would, so id-addressed edits hit the intended step.</summary>
    private static void AssignIds(WorkflowTemplate version)
    {
        var id = 1;
        foreach (var step in version.Steps)
        {
            typeof(WorkflowTemplateStep).GetProperty(nameof(WorkflowTemplateStep.WorkflowTemplateStepId))!.SetValue(step, id++);
        }
    }

    [Fact]
    public void A_catalog_code_can_be_set_once_and_never_changed()
    {
        var requestType = NewRequestType();

        requestType.SetCatalogDetails(" CS-GEN-001 ", "General Inquiries", "Description", """["Doc"]""");
        requestType.AssignCode("CS-GEN-001"); // idempotent

        Assert.Equal("CS-GEN-001", requestType.Code);
        Assert.Equal("General Inquiries", requestType.RequestGroup);
        Assert.Throws<InvalidOperationException>(() => requestType.AssignCode("CS-GEN-002"));
        Assert.Throws<ArgumentException>(() => NewRequestType().AssignCode(new string('X', RequestType.CodeMaxLength + 1)));
    }

    [Fact]
    public void An_administration_update_never_touches_the_catalog_identity()
    {
        var requestType = NewRequestType();
        requestType.SetCatalogDetails("CS-GEN-001", "Group", "Description", null);

        requestType.Update("Renamed", 2, (byte)PriorityLevel.Low, true, true, false, false, null);

        Assert.Equal("CS-GEN-001", requestType.Code);
        Assert.Equal("Description", requestType.Description);
    }

    [Fact]
    public void First_response_unit_defaults_to_the_row_unit_and_survives_an_administration_edit()
    {
        var legacy = new RequestTypeSlaPolicy(1, (byte)PriorityLevel.Medium, SlaTriggerType.TicketCreated, SlaDurationUnit.Days, null, null, 10, 12);
        Assert.Null(legacy.FirstResponseUnit);
        Assert.Equal(SlaDurationUnit.Days, legacy.EffectiveFirstResponseUnit);

        var imported = new RequestTypeSlaPolicy(
            1, (byte)PriorityLevel.Medium, SlaTriggerType.TicketCreated, SlaDurationUnit.Days, 4, null, 1, null,
            clockBasis: SlaClockBasis.BusinessHours, firstResponseUnit: SlaDurationUnit.Hours);
        imported.Update(SlaTriggerType.TicketCreated, SlaDurationUnit.Days, 3, null, 2, null, false, SlaClockBasis.BusinessHours, null, null, null, true);

        Assert.Equal(SlaDurationUnit.Hours, imported.EffectiveFirstResponseUnit);
        Assert.Equal(3, imported.FirstResponseTargetValue);
    }

    [Fact]
    public void Only_a_department_queue_step_can_name_a_department()
    {
        var draft = NewDraft();

        var queue = draft.AppendStep("Handoff to Handover", WorkflowStepKind.Assigned, departmentId: 4);
        Assert.Equal(4, queue.DepartmentId);

        Assert.Throws<WorkflowStepConfigurationException>(() => draft.AppendStep("Work", WorkflowStepKind.InProgress, departmentId: 4));
    }

    [Fact]
    public void Changing_a_queue_step_to_another_kind_drops_its_department()
    {
        var draft = NewDraft();
        draft.AppendStep("Start", WorkflowStepKind.Created);
        var queue = draft.AppendStep("Handoff", WorkflowStepKind.Assigned, departmentId: 4);
        AssignIds(draft);

        draft.UpdateStep(queue.WorkflowTemplateStepId, "Work", WorkflowStepKind.InProgress, false, null);

        Assert.Null(queue.DepartmentId);
    }

    [Fact]
    public void A_new_version_copies_each_steps_department()
    {
        var source = NewDraft();
        source.AppendStep("Start", WorkflowStepKind.Created);
        source.AppendStep("Customer Service Queue", WorkflowStepKind.Assigned, departmentId: 1);
        source.AppendStep("Handoff to Handover", WorkflowStepKind.Assigned, isOptional: true, departmentId: 4);
        source.AppendStep("Resolve", WorkflowStepKind.Resolved);
        source.AppendStep("Close", WorkflowStepKind.Closed);

        var copy = new WorkflowTemplate(1, 2, "T-V2", "T", null, false, false, false, Now, null);
        copy.CopyStepsFrom(source);

        Assert.Equal([null, 1, 4, null, null], copy.Steps.Select(s => s.DepartmentId));
        Assert.True(copy.Steps[2].IsOptional);
    }

    [Fact]
    public void Setting_a_step_department_is_draft_only()
    {
        var version = NewDraft();
        version.AppendStep("Start", WorkflowStepKind.Created);
        var queue = version.AppendStep("Queue", WorkflowStepKind.Assigned);
        version.AppendStep("Resolve", WorkflowStepKind.Resolved);
        version.AppendStep("Close", WorkflowStepKind.Closed);
        AssignIds(version);
        version.SetStepDepartment(queue.WorkflowTemplateStepId, 3);
        Assert.Equal(3, queue.DepartmentId);

        version.Publish(Now, null);

        Assert.Throws<WorkflowVersionNotEditableException>(() => version.SetStepDepartment(queue.WorkflowTemplateStepId, 5));
    }
}

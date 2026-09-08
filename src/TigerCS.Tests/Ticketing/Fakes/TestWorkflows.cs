using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Tests.Ticketing.Fakes;

/// <summary>
/// Builders for workflow VERSIONS in fake-repository tests (Administration /
/// Workflow Designer phase). A request type now points at a logical
/// workflow id; the version the ticket pins is the workflow's Published
/// version, so every test that classifies a ticket needs one of these.
/// </summary>
public static class TestWorkflows
{
    public static readonly DateTime Stamp = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A Draft carrying the phase-1 "With Pending" step shape.</summary>
    public static WorkflowTemplate PendingDraft(int workflowId, int versionNumber = 1, string code = "PENDING")
    {
        var version = new WorkflowTemplate(
            workflowId, versionNumber, versionNumber == 1 ? code : $"{code}-V{versionNumber}", "Request With Pending", null,
            allowsPendingCustomer: true, allowsPendingInternal: true, requiresApproval: false, Stamp, createdByEmployeeId: null);
        version.AppendStep("Ticket Created", WorkflowStepKind.Created);
        version.AppendStep("Assigned", WorkflowStepKind.Assigned);
        version.AppendStep("In Progress", WorkflowStepKind.InProgress);
        version.AppendStep("Pending Customer", WorkflowStepKind.PendingCustomer, isOptional: true);
        version.AppendStep("Pending Internal / Third Party", WorkflowStepKind.PendingInternal, isOptional: true);
        version.AppendStep("Resolved", WorkflowStepKind.Resolved);
        version.AppendStep("Closed", WorkflowStepKind.Closed);
        return version;
    }

    /// <summary>A Draft carrying the phase-1 "Standard" step shape with the given capability flags.</summary>
    public static WorkflowTemplate StandardDraft(
        int workflowId, int versionNumber = 1, string code = "STANDARD",
        bool allowsPendingCustomer = false, bool allowsPendingInternal = false, bool requiresApproval = false)
    {
        var version = new WorkflowTemplate(
            workflowId, versionNumber, versionNumber == 1 ? code : $"{code}-V{versionNumber}", "Standard Request", null,
            allowsPendingCustomer, allowsPendingInternal, requiresApproval, Stamp, createdByEmployeeId: null);
        version.AppendStep("Ticket Created", WorkflowStepKind.Created);
        version.AppendStep("Assigned", WorkflowStepKind.Assigned);
        version.AppendStep("In Progress", WorkflowStepKind.InProgress);
        version.AppendStep("Resolved", WorkflowStepKind.Resolved);
        version.AppendStep("Closed", WorkflowStepKind.Closed);
        return version;
    }

    /// <summary>A Published "With Pending" version — what a classified ticket pins in these tests.</summary>
    public static WorkflowTemplate PublishedPending(int workflowId, int versionNumber = 1, string code = "PENDING")
    {
        var version = PendingDraft(workflowId, versionNumber, code);
        version.Publish(Stamp, publishedByEmployeeId: null);
        return version;
    }

    public static WorkflowTemplate PublishedStandard(
        int workflowId, int versionNumber = 1, string code = "STANDARD",
        bool allowsPendingCustomer = false, bool allowsPendingInternal = false, bool requiresApproval = false)
    {
        var version = StandardDraft(workflowId, versionNumber, code, allowsPendingCustomer, allowsPendingInternal, requiresApproval);
        version.Publish(Stamp, publishedByEmployeeId: null);
        return version;
    }
}

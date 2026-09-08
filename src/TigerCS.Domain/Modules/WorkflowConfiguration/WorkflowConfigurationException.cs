namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>Base of every workflow-configuration rule violation, so the API layer can map the family to one problem shape.</summary>
public abstract class WorkflowConfigurationException(string message) : Exception(message);

/// <summary>A Published or Historical version was asked to change — versions are immutable after publication; create a new version instead.</summary>
public sealed class WorkflowVersionNotEditableException(int workflowTemplateId, WorkflowVersionStatus status)
    : WorkflowConfigurationException(
        $"Workflow version {workflowTemplateId} is {status} and cannot be modified — published versions are immutable; create a new version instead.")
{
    public int WorkflowTemplateId { get; } = workflowTemplateId;
    public WorkflowVersionStatus Status { get; } = status;
}

/// <summary>Publication was refused because the Draft is structurally invalid; <see cref="Issues"/> lists every problem in management-readable wording.</summary>
public sealed class WorkflowVersionInvalidException(int workflowTemplateId, IReadOnlyList<WorkflowValidationIssue> issues)
    : WorkflowConfigurationException(
        $"Workflow version {workflowTemplateId} cannot be published: " + string.Join(" ", issues.Select(i => i.Message)))
{
    public int WorkflowTemplateId { get; } = workflowTemplateId;
    public IReadOnlyList<WorkflowValidationIssue> Issues { get; } = issues;
}

/// <summary>A step id did not belong to the version being edited.</summary>
public sealed class WorkflowStepNotFoundException(int workflowTemplateId, int workflowTemplateStepId)
    : WorkflowConfigurationException($"Step {workflowTemplateStepId} does not belong to workflow version {workflowTemplateId}.")
{
    public int WorkflowTemplateId { get; } = workflowTemplateId;
    public int WorkflowTemplateStepId { get; } = workflowTemplateStepId;
}

/// <summary>A step edit contradicted its kind's configuration rules (e.g. an outcome branch on a step that carries no decision).</summary>
public sealed class WorkflowStepConfigurationException(string message) : WorkflowConfigurationException(message);

namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// The controlled outcomes a step can branch on. Only decision-carrying
/// steps (<see cref="WorkflowStepKind.WaitingForApproval"/>) may configure
/// them; every other step simply proceeds to the next step in sequence.
/// Deliberately a closed enum — never a free-text condition, expression or
/// script.
/// </summary>
public enum WorkflowStepOutcome : byte
{
    /// <summary>The approval was granted (the phase-3 <c>ApprovalReceived</c> / <c>CustomerServiceApproved</c> events).</summary>
    Approved = 1,

    /// <summary>The approval was rejected (the phase-3 <c>ApprovalRejected</c> event).</summary>
    Rejected = 2
}

/// <summary>
/// One configured outcome → target-step edge of a decision step, within
/// the same workflow version. Targets are object references (not stored
/// sequences) so reordering a Draft never silently re-points a branch, and
/// copying a version into a new Draft re-links by position.
/// </summary>
public class WorkflowStepTransition
{
    public int WorkflowStepTransitionId { get; private set; }
    public int WorkflowTemplateStepId { get; private set; }
    public WorkflowStepOutcome Outcome { get; private set; }
    public int TargetWorkflowTemplateStepId { get; private set; }

    /// <summary>Navigation to the owning (source) step — set by that step's own configuration method, never independently.</summary>
    public WorkflowTemplateStep? Step { get; private set; }

    /// <summary>Navigation to the step this outcome leads to.</summary>
    public WorkflowTemplateStep? TargetStep { get; private set; }

    private WorkflowStepTransition() { }

    internal WorkflowStepTransition(WorkflowTemplateStep step, WorkflowStepOutcome outcome, WorkflowTemplateStep targetStep)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentException($"Outcome {outcome} is not a defined workflow step outcome.", nameof(outcome));
        }

        Step = step;
        Outcome = outcome;
        TargetStep = targetStep;
    }

    internal void Retarget(WorkflowTemplateStep targetStep) => TargetStep = targetStep;
}

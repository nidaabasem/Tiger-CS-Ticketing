namespace TigerCS.Domain.Modules.WorkflowConfiguration;

public enum WorkflowValidationSeverity : byte
{
    /// <summary>Blocks publication.</summary>
    Error = 1,

    /// <summary>Shown in the designer; does not block publication.</summary>
    Warning = 2
}

/// <summary>One publish-validation finding in management-readable wording; <paramref name="StepSequence"/> points at the offending step when there is one.</summary>
public sealed record WorkflowValidationIssue(WorkflowValidationSeverity Severity, byte? StepSequence, string Message);

/// <summary>
/// Structural publish validation for a workflow version — the rules a Draft
/// must satisfy before any ticket can be pinned to it. Every rule here is a
/// structural property of the controlled step model (start, terminal path,
/// reachability, branch targets, per-kind configuration); no business-
/// specific decision that has not been confirmed is encoded.
/// </summary>
public static class WorkflowVersionValidator
{
    public static IReadOnlyList<WorkflowValidationIssue> Validate(WorkflowTemplate version)
    {
        ArgumentNullException.ThrowIfNull(version);

        var issues = new List<WorkflowValidationIssue>();
        var steps = version.Steps;

        if (!version.IsDraft)
        {
            issues.Add(Error(null, $"This version is {version.Status} and cannot be modified or published again; create a new version instead."));
        }

        if (steps.Count == 0)
        {
            issues.Add(Error(null, "The workflow has no steps. Add at least a Start step, a Resolve step and a Close step."));
            return issues;
        }

        // ---- ordering data ------------------------------------------------
        var duplicateSequences = steps.GroupBy(s => s.Sequence).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        foreach (var sequence in duplicateSequences)
        {
            issues.Add(Error(sequence, $"More than one step has position {sequence}; step positions must be unique."));
        }

        // ---- start --------------------------------------------------------
        var startSteps = steps.Where(s => s.Kind == WorkflowStepKind.Created).ToList();
        if (startSteps.Count == 0)
        {
            issues.Add(Error(null, "The workflow has no Start step. The first step must be the Start step (ticket created)."));
        }
        else
        {
            if (startSteps.Count > 1)
            {
                issues.Add(Error(startSteps[1].Sequence, "Only one Start step is allowed."));
            }

            if (steps[0].Kind != WorkflowStepKind.Created)
            {
                issues.Add(Error(steps[0].Sequence, $"The first step must be the Start step, but step 1 is '{steps[0].Name}' ({WorkflowStepKinds.Describe(steps[0].Kind).Label})."));
            }
        }

        // ---- terminal path --------------------------------------------------
        var closeSteps = steps.Where(s => s.Kind == WorkflowStepKind.Closed).ToList();
        var resolveSteps = steps.Where(s => s.Kind == WorkflowStepKind.Resolved).ToList();

        if (closeSteps.Count == 0)
        {
            issues.Add(Error(null, "The workflow has no Close step, so tickets could never complete. Add a Close step as the last step."));
        }
        else
        {
            if (closeSteps.Count > 1)
            {
                issues.Add(Error(closeSteps[1].Sequence, "Only one Close step is allowed."));
            }

            if (steps[^1].Kind != WorkflowStepKind.Closed)
            {
                issues.Add(Error(steps[^1].Sequence, $"The Close step must be the last step, but the last step is '{steps[^1].Name}'."));
            }
        }

        if (resolveSteps.Count == 0)
        {
            issues.Add(Error(null, "The workflow has no Resolve step. A ticket is resolved by its department before Customer Service closes it."));
        }
        else if (closeSteps.Count > 0 && resolveSteps.All(r => r.Sequence > closeSteps[0].Sequence))
        {
            issues.Add(Error(resolveSteps[0].Sequence, "The Resolve step must come before the Close step."));
        }

        if (steps[0].Kind is WorkflowStepKind.Resolved or WorkflowStepKind.Closed)
        {
            issues.Add(Error(steps[0].Sequence, "The workflow cannot begin with a Resolve or Close step."));
        }

        // ---- per-kind configuration -----------------------------------------
        foreach (var step in steps)
        {
            if (!WorkflowStepKinds.IsSupported(step.Kind))
            {
                issues.Add(Error(step.Sequence, $"Step '{step.Name}' has an unsupported step type."));
                continue;
            }

            var info = WorkflowStepKinds.Describe(step.Kind);
            if (info.RequiresApprovalType)
            {
                if (step.ApprovalType is not { } approvalType)
                {
                    issues.Add(Error(step.Sequence, $"Approval step '{step.Name}' has no approval type. Choose Accounting Approval or Customer Service Approval."));
                }
                else if (!Enum.IsDefined(approvalType))
                {
                    issues.Add(Error(step.Sequence, $"Approval step '{step.Name}' has an approval type that is not one of the supported types."));
                }
            }

            if (!info.SupportsOutcomeBranches && step.Transitions.Count > 0)
            {
                issues.Add(Error(step.Sequence, $"Step '{step.Name}' is a {info.Label} step and cannot have Approved/Rejected branches."));
            }

            if (step.Kind == WorkflowStepKind.Closed && step.IsOptional)
            {
                issues.Add(Error(step.Sequence, "The Close step cannot be optional."));
            }

            if (step.Kind == WorkflowStepKind.Created && step.IsOptional)
            {
                issues.Add(Error(step.Sequence, "The Start step cannot be optional."));
            }

            var duplicateOutcomes = step.Transitions.GroupBy(t => t.Outcome).Where(g => g.Count() > 1).Select(g => g.Key);
            foreach (var outcome in duplicateOutcomes)
            {
                issues.Add(Error(step.Sequence, $"Step '{step.Name}' configures the {outcome} outcome more than once."));
            }

            foreach (var transition in step.Transitions)
            {
                if (transition.TargetStep is not { } target || !steps.Contains(target))
                {
                    issues.Add(Error(step.Sequence, $"Step '{step.Name}': the {transition.Outcome} branch points at a step that no longer exists."));
                }
                else if (ReferenceEquals(target, step))
                {
                    issues.Add(Error(step.Sequence, $"Step '{step.Name}': the {transition.Outcome} branch cannot point back at the same step."));
                }
                else if (transition.Outcome == WorkflowStepOutcome.Approved && target.Kind == WorkflowStepKind.Created)
                {
                    issues.Add(Error(step.Sequence, $"Step '{step.Name}': the Approved branch cannot return to the Start step."));
                }
            }
        }

        // ---- reachability -------------------------------------------------------
        if (startSteps.Count > 0 && steps[0].Kind == WorkflowStepKind.Created && duplicateSequences.Count == 0)
        {
            var reachable = Reachable(steps);
            foreach (var step in steps.Where(s => !reachable.Contains(s)))
            {
                issues.Add(Error(step.Sequence, $"Step '{step.Name}' can never be reached from the Start step. Move it, branch to it, or delete it."));
            }

            if (closeSteps.Count > 0 && !reachable.Contains(closeSteps[0]))
            {
                issues.Add(Error(closeSteps[0].Sequence, "The Close step can never be reached from the Start step, so no ticket could complete."));
            }
        }

        // ---- non-blocking guidance ----------------------------------------------
        foreach (var step in steps.Where(s => s.Kind == WorkflowStepKind.WaitingForApproval && s.Transitions.All(t => t.Outcome != WorkflowStepOutcome.Rejected)))
        {
            issues.Add(Warning(step.Sequence, $"Approval step '{step.Name}' has no Rejected branch: after a rejection the next action stays explicit (nothing happens automatically)."));
        }

        return issues;
    }

    /// <summary>
    /// The set of steps reachable from the first step: each non-terminal step
    /// flows to the next in sequence (an Approval step's Approved branch
    /// replaces that default when configured), and every configured branch
    /// is an edge. A Rejected branch never counts as the way forward on its
    /// own, but the step it points at is reachable.
    /// </summary>
    private static HashSet<WorkflowTemplateStep> Reachable(IReadOnlyList<WorkflowTemplateStep> steps)
    {
        var reachable = new HashSet<WorkflowTemplateStep>();
        var pending = new Stack<WorkflowTemplateStep>();
        pending.Push(steps[0]);

        while (pending.Count > 0)
        {
            var step = pending.Pop();
            if (!reachable.Add(step))
            {
                continue;
            }

            if (step.Kind == WorkflowStepKind.Closed)
            {
                continue;
            }

            var index = steps.ToList().IndexOf(step);
            var approved = step.Transitions.FirstOrDefault(t => t.Outcome == WorkflowStepOutcome.Approved)?.TargetStep;
            var next = approved ?? (index + 1 < steps.Count ? steps[index + 1] : null);
            if (next is not null)
            {
                pending.Push(next);
            }

            foreach (var transition in step.Transitions)
            {
                if (transition.TargetStep is { } target && steps.Contains(target))
                {
                    pending.Push(target);
                }
            }
        }

        return reachable;
    }

    private static WorkflowValidationIssue Error(byte? sequence, string message) =>
        new(WorkflowValidationSeverity.Error, sequence, message);

    private static WorkflowValidationIssue Warning(byte? sequence, string message) =>
        new(WorkflowValidationSeverity.Warning, sequence, message);
}

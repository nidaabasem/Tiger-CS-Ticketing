namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// How a ticket's position moves through its pinned workflow version when
/// its request type has <see cref="RequestType.ConfigurationEnforced"/> on.
/// Pure rules over the version's steps — no new status, no new action:
/// each move is triggered by an EXISTING lifecycle action, which keeps its
/// own authorization and audit; this only decides whether that action is
/// the one the workflow expects next, and which step it lands on.
///
/// <para>
/// <b>The moves.</b> A ticket enters at the Department Queue step of its
/// own department right after Start. Assigning an owner while at a queue
/// step moves it to the following work step. A Transfer must target the
/// department of the next queue step (a handoff, or a return to Customer
/// Service). Resolve (outcome Resolved) and Close must each be the next
/// step. A Cancelled / Rejected / Duplicate resolution ends the request
/// wherever it is, landing on the Resolve step. Reopen lands on the last
/// queue step of the reopen's target department.
/// </para>
///
/// <para>
/// <b>"Next" skips optional steps</b> (a conditional handoff may be
/// bypassed) and Pending Customer steps, which mark where waiting on the
/// customer is expected but are a status within the work, not a position.
/// </para>
/// </summary>
public static class WorkflowProgression
{
    /// <summary>The step kinds the runtime can track. A version with any other kind cannot be enforced.</summary>
    public static IReadOnlySet<WorkflowStepKind> SupportedKinds { get; } = new HashSet<WorkflowStepKind>
    {
        WorkflowStepKind.Created,
        WorkflowStepKind.Assigned,
        WorkflowStepKind.InProgress,
        WorkflowStepKind.PendingCustomer,
        WorkflowStepKind.Resolved,
        WorkflowStepKind.Closed
    };

    /// <summary>Where a new ticket in <paramref name="departmentId"/> starts: the queue step right after Start.</summary>
    public static WorkflowTemplateStep? Entry(WorkflowTemplate version, int departmentId)
    {
        ArgumentNullException.ThrowIfNull(version);
        var start = version.Steps.FirstOrDefault(s => s.Kind == WorkflowStepKind.Created);
        return start is null ? null : Next(version, start, s => IsQueueOf(s, departmentId));
    }

    /// <summary>At a queue step, an owner being assigned moves the ticket to the following work step; anywhere else it moves nothing.</summary>
    public static WorkflowTemplateStep? AfterOwnerAssigned(WorkflowTemplate version, WorkflowTemplateStep current) =>
        current.Kind == WorkflowStepKind.Assigned ? Next(version, current, s => s.Kind == WorkflowStepKind.InProgress) : null;

    /// <summary>The queue step a transfer to <paramref name="targetDepartmentId"/> lands on, or null when the workflow does not expect that handoff now.</summary>
    public static WorkflowTemplateStep? ForTransfer(WorkflowTemplate version, WorkflowTemplateStep current, int targetDepartmentId) =>
        Next(version, current, s => IsQueueOf(s, targetDepartmentId));

    /// <summary>
    /// The Resolve step a resolution lands on. <paramref name="completesWork"/>
    /// is true for outcome Resolved, which must be the next step; the other
    /// outcomes end the request from wherever it is.
    /// </summary>
    public static WorkflowTemplateStep? ForResolve(WorkflowTemplate version, WorkflowTemplateStep current, bool completesWork) =>
        completesWork
            ? Next(version, current, s => s.Kind == WorkflowStepKind.Resolved)
            : version.Steps.FirstOrDefault(s => s.Kind == WorkflowStepKind.Resolved && s.Sequence > current.Sequence)
              ?? (current.Kind == WorkflowStepKind.Resolved ? current : null);

    /// <summary>The Close step, when it is next.</summary>
    public static WorkflowTemplateStep? ForClose(WorkflowTemplate version, WorkflowTemplateStep current) =>
        Next(version, current, s => s.Kind == WorkflowStepKind.Closed);

    /// <summary>The last queue step of <paramref name="targetDepartmentId"/> before Resolve — where a reopened ticket resumes.</summary>
    public static WorkflowTemplateStep? ForReopen(WorkflowTemplate version, int targetDepartmentId)
    {
        ArgumentNullException.ThrowIfNull(version);
        var resolve = version.Steps.FirstOrDefault(s => s.Kind == WorkflowStepKind.Resolved);
        return version.Steps
            .Where(s => IsQueueOf(s, targetDepartmentId) && (resolve is null || s.Sequence < resolve.Sequence))
            .LastOrDefault();
    }

    /// <summary>The step(s) the workflow expects next, for an error message ("Handoff to Handover").</summary>
    public static string DescribeExpectedNext(WorkflowTemplate version, WorkflowTemplateStep current)
    {
        var expected = new List<string>();
        foreach (var step in version.Steps.Where(s => s.Sequence > current.Sequence))
        {
            if (step.Kind == WorkflowStepKind.PendingCustomer)
            {
                continue;
            }

            expected.Add(step.Name + (step.IsOptional ? " (optional)" : string.Empty));
            if (!step.IsOptional)
            {
                break;
            }
        }

        return expected.Count == 0 ? "nothing — the workflow is complete" : string.Join(" or ", expected);
    }

    private static bool IsQueueOf(WorkflowTemplateStep step, int departmentId) =>
        step.Kind == WorkflowStepKind.Assigned && step.DepartmentId == departmentId;

    private static WorkflowTemplateStep? Next(WorkflowTemplate version, WorkflowTemplateStep current, Func<WorkflowTemplateStep, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(current);

        foreach (var step in version.Steps.Where(s => s.Sequence > current.Sequence))
        {
            if (matches(step))
            {
                return step;
            }

            if (step.IsOptional || step.Kind == WorkflowStepKind.PendingCustomer)
            {
                continue;
            }

            return null;
        }

        return null;
    }
}

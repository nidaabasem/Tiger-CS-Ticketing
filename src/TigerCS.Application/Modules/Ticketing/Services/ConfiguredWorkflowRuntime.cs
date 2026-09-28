using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>The outcome of checking an action against a ticket's tracked workflow.</summary>
/// <param name="Allowed">Whether the action may proceed.</param>
/// <param name="Target">The step the action lands on; null when the ticket is not tracked (nothing to move).</param>
/// <param name="Reason">Why it was refused — names the step the workflow expects next.</param>
public sealed record WorkflowStepCheck(bool Allowed, WorkflowTemplateStep? Target, string? Reason)
{
    public static WorkflowStepCheck Untracked { get; } = new(true, null, null);
}

/// <summary>
/// Runtime tracking of a ticket's position in its pinned workflow version —
/// only for tickets whose request type has
/// <see cref="RequestType.ConfigurationEnforced"/> on and that entered the
/// workflow while it was on. Every other ticket is untracked and every check
/// answers "allowed", which is exactly the pre-existing behaviour.
///
/// <para>
/// <b>Adds validation, never authority.</b> Callers run their existing
/// authorization, closed-ticket and department-setting checks first and
/// consult this only afterwards, so a caller the existing rules refuse is
/// refused exactly as before, and a caller they allow may additionally be
/// told the workflow expects a different step. Department handoffs remain
/// the existing Transfer action (CS Manager only); this never moves a
/// ticket's department itself.
/// </para>
///
/// <para>
/// Every step change is audited ("WorkflowStep") under the triggering
/// action's correlation id, so the step history reads as part of that action.
/// </para>
/// </summary>
public sealed class ConfiguredWorkflowRuntime(
    IRequestTypeRepository requestTypeRepository,
    IWorkflowTemplateRepository workflowTemplateRepository,
    IAuditEntryWriter auditWriter)
{
    private sealed record Tracked(WorkflowTemplate Version, WorkflowTemplateStep? Current);

    /// <summary>
    /// Places a newly pinned ticket at its entry step (the queue step of its
    /// department). Call after the ticket row exists and its version is
    /// pinned; a no-op for untracked tickets.
    /// </summary>
    public async Task InitializeAsync(Ticket ticket, Guid? actorEmployeeId, Guid correlationId, CancellationToken cancellationToken)
    {
        var tracked = await ResolveAsync(ticket, requireCurrent: false, cancellationToken);
        if (tracked is null || tracked.Current is not null)
        {
            return;
        }

        var entry = WorkflowProgression.Entry(tracked.Version, ticket.CurrentDepartmentId)
            ?? throw new InvalidOperationException(
                $"Workflow version {tracked.Version.WorkflowTemplateId} has no entry queue step for department {ticket.CurrentDepartmentId}; "
                + "readiness validation should have prevented enforcing it.");

        await MoveAsync(ticket, tracked.Version, current: null, entry, "TicketCreated", actorEmployeeId, correlationId, cancellationToken);
        await OnOwnerAssignedAsync(ticket, actorEmployeeId, correlationId, cancellationToken);
    }

    /// <summary>After any owner assignment (manual or automatic): a ticket at a queue step moves to its work step.</summary>
    public async Task OnOwnerAssignedAsync(Ticket ticket, Guid? actorEmployeeId, Guid correlationId, CancellationToken cancellationToken)
    {
        if (ticket.CurrentOwnerEmployeeId is null)
        {
            return;
        }

        var tracked = await ResolveAsync(ticket, requireCurrent: true, cancellationToken);
        if (tracked?.Current is not { } current)
        {
            return;
        }

        if (WorkflowProgression.AfterOwnerAssigned(tracked.Version, current) is { } work)
        {
            await MoveAsync(ticket, tracked.Version, current, work, "Assigned", actorEmployeeId, correlationId, cancellationToken);
        }
    }

    public Task<WorkflowStepCheck> CheckTransferAsync(Ticket ticket, int targetDepartmentId, CancellationToken cancellationToken) =>
        CheckAsync(ticket, requireCurrent: true, (version, current) => WorkflowProgression.ForTransfer(version, current!, targetDepartmentId),
            "transfer to this department", cancellationToken);

    public Task<WorkflowStepCheck> CheckResolveAsync(Ticket ticket, ResolutionOutcome outcome, CancellationToken cancellationToken) =>
        CheckAsync(ticket, requireCurrent: true,
            (version, current) => WorkflowProgression.ForResolve(version, current!, completesWork: outcome == ResolutionOutcome.Resolved),
            "resolve", cancellationToken);

    public Task<WorkflowStepCheck> CheckCloseAsync(Ticket ticket, CancellationToken cancellationToken) =>
        CheckAsync(ticket, requireCurrent: true, (version, current) => WorkflowProgression.ForClose(version, current!), "close", cancellationToken);

    public Task<WorkflowStepCheck> CheckReopenAsync(Ticket ticket, int targetDepartmentId, CancellationToken cancellationToken) =>
        CheckAsync(ticket, requireCurrent: true, (version, _) => WorkflowProgression.ForReopen(version, targetDepartmentId),
            "reopen into this department", cancellationToken);

    /// <summary>Moves the ticket to a checked target step; a no-op for an untracked ticket.</summary>
    public async Task ApplyAsync(
        Ticket ticket, WorkflowStepCheck check, string trigger, Guid? actorEmployeeId, Guid correlationId, CancellationToken cancellationToken)
    {
        if (check.Target is not { } target)
        {
            return;
        }

        var tracked = await ResolveAsync(ticket, requireCurrent: true, cancellationToken);
        if (tracked is null)
        {
            return;
        }

        await MoveAsync(ticket, tracked.Version, tracked.Current, target, trigger, actorEmployeeId, correlationId, cancellationToken);
    }

    private async Task<WorkflowStepCheck> CheckAsync(
        Ticket ticket,
        bool requireCurrent,
        Func<WorkflowTemplate, WorkflowTemplateStep?, WorkflowTemplateStep?> resolveTarget,
        string action,
        CancellationToken cancellationToken)
    {
        var tracked = await ResolveAsync(ticket, requireCurrent, cancellationToken);
        if (tracked?.Current is not { } current)
        {
            return WorkflowStepCheck.Untracked;
        }

        var target = resolveTarget(tracked.Version, current);
        return target is not null
            ? new WorkflowStepCheck(true, target, null)
            : new WorkflowStepCheck(false, null,
                $"The ticket's workflow is at '{current.Name}'; it cannot {action} now. "
                + $"Expected next: {WorkflowProgression.DescribeExpectedNext(tracked.Version, current)}.");
    }

    /// <summary>The ticket's pinned version and current step, or null when the ticket is not tracked.</summary>
    private async Task<Tracked?> ResolveAsync(Ticket ticket, bool requireCurrent, CancellationToken cancellationToken)
    {
        if (ticket.RequestTypeId is not { } requestTypeId || ticket.WorkflowTemplateId is not { } versionId)
        {
            return null;
        }

        // A ticket that entered its workflow before enforcement was switched
        // on has no current step and stays untracked: enabling enforcement
        // never retroactively constrains tickets already in flight.
        if (requireCurrent && ticket.CurrentWorkflowStepId is null)
        {
            return null;
        }

        var requestType = await requestTypeRepository.GetByIdAsync(requestTypeId, cancellationToken);
        if (requestType is not { ConfigurationEnforced: true })
        {
            return null;
        }

        var version = await workflowTemplateRepository.GetByIdAsync(versionId, cancellationToken);
        if (version is null)
        {
            return null;
        }

        var current = ticket.CurrentWorkflowStepId is { } stepId
            ? version.Steps.FirstOrDefault(s => s.WorkflowTemplateStepId == stepId)
            : null;
        return new Tracked(version, current);
    }

    private async Task MoveAsync(
        Ticket ticket, WorkflowTemplate version, WorkflowTemplateStep? current, WorkflowTemplateStep target,
        string trigger, Guid? actorEmployeeId, Guid correlationId, CancellationToken cancellationToken)
    {
        ticket.MoveToWorkflowStep(target.WorkflowTemplateStepId);
        await auditWriter.WriteAsync(
            actorEmployeeId,
            "WorkflowStep",
            "Ticket",
            ticket.TicketId.ToString(),
            beforeValue: current is null ? null : Describe(current),
            afterValue: $"{Describe(target)};Trigger={trigger};WorkflowTemplateId={version.WorkflowTemplateId}",
            correlationId,
            cancellationToken);
    }

    private static string Describe(WorkflowTemplateStep step) =>
        $"StepId={step.WorkflowTemplateStepId};Sequence={step.Sequence};Step={step.Name}"
        + (step.DepartmentId is { } departmentId ? $";DepartmentId={departmentId}" : string.Empty);
}

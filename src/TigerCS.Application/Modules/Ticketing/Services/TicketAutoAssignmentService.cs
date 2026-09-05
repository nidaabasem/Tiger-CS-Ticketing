using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>How one automatic-assignment attempt ended. Everything except <see cref="AssignedToPrimary"/> leaves the ticket in the department queue — the universal safe fallback; a random employee is never chosen.</summary>
public enum AutoAssignmentOutcome
{
    /// <summary>The ticket carries no request type — no automation applies; exactly the pre-phase-2 behavior.</summary>
    NoRequestType,

    /// <summary>No active assignment rule is configured for the request type — department queue.</summary>
    NoRuleConfigured,

    /// <summary>The rule explicitly says department queue.</summary>
    DepartmentQueueByRule,

    /// <summary>The department's workflow settings disable assignment — department queue.</summary>
    AssignmentDisabledForDepartment,

    /// <summary>The configured primary assignee is not an active member of the ticket's department — degraded safely to the department queue and audited, never guessed.</summary>
    ConfiguredAssigneeNotInDepartment,

    /// <summary>The ticket was assigned to the rule's primary assignee.</summary>
    AssignedToPrimary
}

/// <summary>
/// Why the automation ran. The department is the primary assignment and the
/// employee only the secondary one, so the same rule evaluation happens both
/// when a ticket first gets its department and when a transfer moves it to a
/// new one — the audit trail must still say which of the two produced the
/// outcome.
/// </summary>
public enum AutoAssignmentTrigger
{
    /// <summary>The ticket was just created with its department and request type known.</summary>
    TicketCreated,

    /// <summary>A department transfer moved the ticket; the rule is re-evaluated against the NEW responsible department.</summary>
    DepartmentTransfer
}

/// <summary>The outcome plus what was assigned, for the caller's response/logging. <paramref name="TeamMemberEmployeeIds"/> carries the configured team members (excluding the primary) for a team rule.</summary>
public sealed record AutoAssignmentResult(
    AutoAssignmentOutcome Outcome,
    Guid? AssignedEmployeeId = null,
    IReadOnlyList<Guid>? TeamMemberEmployeeIds = null);

/// <summary>
/// Workflow/Automation phase 2 — executes the configured assignment rule
/// when a ticket is created with its department and request type known, so
/// the Call Center/CS agent never needs to know every department's staff:
/// Department + Request Type → rule → primary assignee, or the department
/// queue where no valid automatic target exists.
///
/// <para>
/// <b>System actions are recorded as system actions.</b> Every automatic
/// assignment writes its <c>TicketAssignments</c> row and audit entry with a
/// <c>null</c> acting employee — never as if the creating agent performed
/// the assignment — and names the rule it applied, so history reads
/// "automatically assigned by rule", distinguishable from any manual
/// (re)assignment.
/// </para>
///
/// <para>
/// <b>Fail-safe by construction.</b> A missing rule, an inactive rule, a
/// department whose settings disable assignment, or a configured assignee
/// who is no longer an active member of the department all degrade to the
/// department queue (unassigned, supervisor assigns manually) with the
/// reason audited. Ownership stays single: a team rule assigns its primary
/// as the one accountable owner; members are configuration the later
/// collaboration phase can surface.
/// </para>
/// </summary>
public sealed class TicketAutoAssignmentService(
    IRequestTypeAssignmentRuleRepository assignmentRuleRepository,
    IDepartmentWorkflowSettingsRepository departmentWorkflowSettingsRepository,
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
    ITicketAssignmentRepository ticketAssignmentRepository,
    IAuditEntryWriter auditWriter)
{
    /// <summary>
    /// Runs inside the caller's creation transaction, after the ticket row
    /// exists (its real TicketId is needed for the assignment/audit rows) —
    /// the caller commits or rolls everything back together.
    /// </summary>
    public async Task<AutoAssignmentResult> ApplyAsync(
        Ticket ticket,
        DateTime nowUtc,
        Guid correlationId,
        AutoAssignmentTrigger trigger = AutoAssignmentTrigger.TicketCreated,
        CancellationToken cancellationToken = default)
    {
        if (ticket.RequestTypeId is not { } requestTypeId)
        {
            return new AutoAssignmentResult(AutoAssignmentOutcome.NoRequestType);
        }

        var settings = await departmentWorkflowSettingsRepository.GetByDepartmentIdAsync(
            ticket.CurrentDepartmentId, cancellationToken);
        if (settings is { AllowAssignment: false })
        {
            return await QueueAsync(ticket, AutoAssignmentOutcome.AssignmentDisabledForDepartment,
                "Department workflow settings disable assignment", trigger, correlationId, cancellationToken);
        }

        var rule = await assignmentRuleRepository.GetByRequestTypeIdAsync(requestTypeId, cancellationToken);
        if (rule is null || !rule.IsActive)
        {
            return await QueueAsync(ticket, AutoAssignmentOutcome.NoRuleConfigured,
                "No active assignment rule configured", trigger, correlationId, cancellationToken);
        }

        if (rule.Mode == AssignmentMode.DepartmentQueue || rule.PrimaryEmployeeId is not { } primaryEmployeeId)
        {
            return await QueueAsync(ticket, AutoAssignmentOutcome.DepartmentQueueByRule,
                $"Rule {rule.RequestTypeAssignmentRuleId} routes to the department queue", trigger, correlationId, cancellationToken);
        }

        // The configured target is re-validated at assignment time against
        // the ticket's actual current department — configuration that went
        // stale (the employee left the department) degrades to the queue.
        if (!await userDepartmentAssignmentRepository.ExistsAsync(primaryEmployeeId, ticket.CurrentDepartmentId, cancellationToken))
        {
            return await QueueAsync(ticket, AutoAssignmentOutcome.ConfiguredAssigneeNotInDepartment,
                $"Rule {rule.RequestTypeAssignmentRuleId}'s primary assignee is not an active member of department {ticket.CurrentDepartmentId}",
                trigger, correlationId, cancellationToken);
        }

        var previousOwnerEmployeeId = ticket.CurrentOwnerEmployeeId;
        ticket.AssignTo(primaryEmployeeId);

        var currentAssignment = await ticketAssignmentRepository.GetCurrentAsync(ticket.TicketId, cancellationToken);
        currentAssignment?.MarkSuperseded();

        // AssigningActorEmployeeId null = the system, not a person — the
        // same distinction the audit entry below makes with a null actor.
        await ticketAssignmentRepository.AddAsync(
            new TicketAssignment(ticket.TicketId, primaryEmployeeId, ticket.CurrentDepartmentId, nowUtc, assigningActorEmployeeId: null),
            cancellationToken);

        var teamMembers = rule.Mode == AssignmentMode.Team
            ? rule.Members.Select(m => m.EmployeeId).ToList()
            : null;

        await auditWriter.WriteAsync(
            actorEmployeeId: null,
            "AutoAssign", "Ticket", ticket.TicketId.ToString(),
            beforeValue: previousOwnerEmployeeId?.ToString(),
            afterValue: $"AssignedEmployeeId={primaryEmployeeId};DepartmentId={ticket.CurrentDepartmentId};Mode={rule.Mode};RuleId={rule.RequestTypeAssignmentRuleId};Trigger={trigger}"
                + (rule.TeamName is { } teamName ? $";Team={teamName}" : string.Empty),
            correlationId, cancellationToken);

        return new AutoAssignmentResult(AutoAssignmentOutcome.AssignedToPrimary, primaryEmployeeId, teamMembers);
    }

    /// <summary>The safe fallback — the ticket stays unassigned in its department queue, and the why is audited as a system action so the queue outcome is as traceable as an assignment.</summary>
    private async Task<AutoAssignmentResult> QueueAsync(
        Ticket ticket,
        AutoAssignmentOutcome outcome,
        string reason,
        AutoAssignmentTrigger trigger,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        await auditWriter.WriteAsync(
            actorEmployeeId: null,
            "AutoAssign", "Ticket", ticket.TicketId.ToString(),
            beforeValue: ticket.CurrentOwnerEmployeeId?.ToString(),
            afterValue: $"DepartmentQueue;DepartmentId={ticket.CurrentDepartmentId};Trigger={trigger};Reason={reason}",
            correlationId, cancellationToken);

        return new AutoAssignmentResult(outcome);
    }
}

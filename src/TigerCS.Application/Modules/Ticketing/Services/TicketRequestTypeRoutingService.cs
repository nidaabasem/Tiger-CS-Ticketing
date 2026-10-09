using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>Why a supplied request type could not be used. Every one of these leaves the ticket exactly as it was.</summary>
public enum RequestTypeResolutionFailure
{
    /// <summary>Neither an id nor a name was supplied.</summary>
    NotSupplied,

    NotFound,
    Inactive,

    /// <summary>The name matches active request types in more than one department; the id is required.</summary>
    Ambiguous,

    /// <summary>The request type's responsible department is missing or inactive, so there is nowhere to route.</summary>
    DepartmentInactive,

    /// <summary>The request type has no Published workflow version, so it cannot govern a ticket.</summary>
    WorkflowNotPublished
}

/// <summary>A supplied request type, resolved and validated — or the precise reason it is not usable.</summary>
public sealed record RequestTypeResolution(
    RequestType? RequestType,
    WorkflowTemplate? PublishedWorkflow,
    RequestTypeResolutionFailure? Failure,
    string? Detail)
{
    public bool IsValid => Failure is null;
}

/// <summary>What applying a request type to a ticket did, for the caller's response and the audit trail.</summary>
public sealed record RequestTypeRoutingResult(
    int RequestTypeId,
    string RequestTypeName,
    int PreviousDepartmentId,
    int DepartmentId,
    bool Transferred,
    Guid? AssignedEmployeeId,
    string AssignmentOutcome,
    string SlaOutcome);

/// <summary>
/// Classifies a request type onto an existing ticket and applies everything
/// the request type configures, in the caller's transaction: its workflow
/// version (pinned), its responsible department (through the same transfer
/// semantics as <c>TicketAssignmentAppService.TransferAsync</c>), the
/// configured automatic assignment, and its SLA policy.
///
/// <para>
/// <b>Used for Genesys tickets, which are created before the request type
/// is known.</b> The ticket is created in the department the interaction
/// arrived in; <c>OriginatingDepartmentId</c> is therefore never rewritten —
/// only <c>CurrentDepartmentId</c> moves, via <see cref="Ticket.TransferToDepartment"/>
/// (which also clears the owner, so the receiving department's rule or queue
/// decides). The routing is a system action driven by configuration, so it is
/// not gated on the CS-Manager-only manual transfer role and does not
/// consult the source department's "allow transfers out" setting; the
/// configured request-type department is the authority.
/// </para>
///
/// <para>
/// <b>Assignment stays automatic and fail-safe.</b> <see cref="TicketAutoAssignmentService"/>
/// picks the configured assignee; no rule, a stale assignee or a department
/// that disables assignment leave the ticket in the responsible
/// department's queue. A ticket that already has an owner and does not move
/// departments keeps its owner.
/// </para>
///
/// <para>
/// <b>The SLA is adjusted, never restarted.</b> See
/// <see cref="SlaDueDateService.ReapplyPolicyForRequestTypeAsync"/>. Routing
/// and assignment never touch <c>FirstHumanResponseAtUtc</c>.
/// </para>
/// </summary>
public sealed class TicketRequestTypeRoutingService(
    IRequestTypeRepository requestTypeRepository,
    IWorkflowTemplateRepository workflowTemplateRepository,
    IDepartmentRepository departmentRepository,
    TicketAutoAssignmentService autoAssignmentService,
    SlaDueDateService slaDueDateService,
    ITicketStatusHistoryRepository statusHistoryRepository,
    IAuditEntryWriter auditWriter)
{
    /// <summary>Resolves a request type by id (preferred) or by exact name, and validates that it can govern a ticket.</summary>
    public async Task<RequestTypeResolution> ResolveAsync(
        int? requestTypeId, string? requestTypeName, CancellationToken cancellationToken = default)
    {
        RequestType? requestType;
        if (requestTypeId is { } id)
        {
            requestType = await requestTypeRepository.GetByIdAsync(id, cancellationToken);
            if (requestType is null)
            {
                return Fail(RequestTypeResolutionFailure.NotFound, $"Request type {id} does not exist.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(requestTypeName))
        {
            var name = requestTypeName.Trim();
            var all = await requestTypeRepository.ListAsync(departmentId: null, includeInactive: true, cancellationToken);
            var matches = all.Where(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            var active = matches.Where(r => r.IsActive).ToList();

            if (active.Count > 1)
            {
                return Fail(RequestTypeResolutionFailure.Ambiguous,
                    $"Request type name '{name}' exists in more than one department; send requestTypeId instead.");
            }

            requestType = active.FirstOrDefault() ?? matches.FirstOrDefault();
            if (requestType is null)
            {
                return Fail(RequestTypeResolutionFailure.NotFound, $"No request type is named '{name}'.");
            }
        }
        else
        {
            return Fail(RequestTypeResolutionFailure.NotSupplied, "No request type was supplied.");
        }

        if (!requestType.IsActive)
        {
            return Fail(RequestTypeResolutionFailure.Inactive, $"Request type '{requestType.Name}' ({requestType.RequestTypeId}) is inactive.");
        }

        var department = await departmentRepository.GetByIdAsync(requestType.DepartmentId, cancellationToken);
        if (department is null || !department.IsActive)
        {
            return Fail(RequestTypeResolutionFailure.DepartmentInactive,
                $"The responsible department ({requestType.DepartmentId}) of request type '{requestType.Name}' is missing or inactive.");
        }

        var workflow = await workflowTemplateRepository.GetPublishedAsync(requestType.WorkflowId, cancellationToken);
        if (workflow is null)
        {
            return Fail(RequestTypeResolutionFailure.WorkflowNotPublished,
                $"Request type '{requestType.Name}' has no published workflow version.");
        }

        return new RequestTypeResolution(requestType, workflow, null, null);
    }

    /// <summary>
    /// Applies a <b>valid</b> resolution to a ticket that has no request type
    /// yet. Throws <see cref="TicketRequestTypeAlreadySetException"/> (domain
    /// guard) when one is set — the caller decides idempotency before calling.
    /// </summary>
    /// <remarks>
    /// <c>defaultPriority</c> is for a ticket that predates default priorities
    /// (no priority, no SLA period): it gets the default so it can be measured
    /// at all, and its clock starts now — nothing is backdated. It is ignored
    /// for a ticket that already has a priority.
    /// </remarks>
    public async Task<RequestTypeRoutingResult> ApplyAsync(
        Ticket ticket,
        RequestTypeResolution resolution,
        Guid actorEmployeeId,
        string source,
        Priority? defaultPriority,
        DateTime nowUtc,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (!resolution.IsValid || resolution.RequestType is null || resolution.PublishedWorkflow is null)
        {
            throw new ArgumentException("A valid request-type resolution is required.", nameof(resolution));
        }

        var requestType = resolution.RequestType;
        var previousDepartmentId = ticket.CurrentDepartmentId;
        var previousOwner = ticket.CurrentOwnerEmployeeId;

        ticket.ClassifyRequestType(requestType.RequestTypeId);
        ticket.PinWorkflowVersion(resolution.PublishedWorkflow.WorkflowTemplateId);

        // Department FIRST, exactly as TransferAsync does: the responsible
        // department is the primary assignment, OriginatingDepartmentId is
        // never touched, and the transfer clears the previous owner.
        var transferred = requestType.DepartmentId != ticket.CurrentDepartmentId;
        if (transferred)
        {
            ticket.TransferToDepartment(requestType.DepartmentId);

            await auditWriter.WriteAsync(
                actorEmployeeId, "Transfer", "Ticket", ticket.TicketId.ToString(),
                beforeValue: $"DepartmentId={previousDepartmentId};AssignedEmployeeId={previousOwner?.ToString() ?? "DepartmentQueue"}",
                afterValue: $"DepartmentId={requestType.DepartmentId};Reason=Request type '{requestType.Name}' ({requestType.RequestTypeId}) routing ({source});OriginatingDepartmentId={ticket.OriginatingDepartmentId} (unchanged)",
                correlationId, cancellationToken);
        }

        // Existing automatic assignment: department + request type -> rule ->
        // employee, else the department queue. A ticket a person already owns
        // (and that did not move) keeps its owner.
        var assignmentOutcome = "OwnerKept";
        Guid? assignedEmployeeId = ticket.CurrentOwnerEmployeeId;
        if (transferred || ticket.CurrentOwnerEmployeeId is null)
        {
            var assignment = await autoAssignmentService.ApplyAsync(
                ticket, nowUtc, correlationId,
                transferred ? AutoAssignmentTrigger.DepartmentTransfer : AutoAssignmentTrigger.RequestTypeClassified,
                cancellationToken);
            assignmentOutcome = assignment.Outcome.ToString();
            assignedEmployeeId = assignment.AssignedEmployeeId;
        }

        var slaOutcome = await ApplySlaAsync(ticket, defaultPriority, actorEmployeeId, nowUtc, correlationId, cancellationToken);

        await auditWriter.WriteAsync(
            actorEmployeeId, "ClassifyRequestType", "Ticket", ticket.TicketId.ToString(),
            beforeValue: $"RequestTypeId=(none);DepartmentId={previousDepartmentId};AssignedEmployeeId={previousOwner?.ToString() ?? "DepartmentQueue"}",
            afterValue:
                $"RequestTypeId={requestType.RequestTypeId};RequestType={requestType.Name};DepartmentId={ticket.CurrentDepartmentId};"
                + $"OriginatingDepartmentId={ticket.OriginatingDepartmentId};Transferred={transferred};"
                + $"Assignment={assignmentOutcome};AssignedEmployeeId={assignedEmployeeId?.ToString() ?? "DepartmentQueue"};"
                + $"Sla={slaOutcome};Source={source}",
            correlationId, cancellationToken);

        return new RequestTypeRoutingResult(
            requestType.RequestTypeId, requestType.Name, previousDepartmentId, ticket.CurrentDepartmentId,
            transferred, assignedEmployeeId, assignmentOutcome, slaOutcome);
    }

    private async Task<string> ApplySlaAsync(
        Ticket ticket, Priority? defaultPriority, Guid actorEmployeeId, DateTime nowUtc, Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (ticket.PriorityId is null)
        {
            if (defaultPriority is null)
            {
                return "NoPriority";
            }

            // A ticket from before default priorities: give it the default
            // and start its clock now (never backdated), request-type policy
            // included because the request type is already set.
            ticket.ApplyDefaultPriority(defaultPriority.PriorityId);
            await slaDueDateService.OpenInitialPeriodAsync(ticket, nowUtc, actorEmployeeId, correlationId, cancellationToken);

            var previousState = ticket.SlaState;
            ticket.StartSlaClock();
            if (previousState != ticket.SlaState)
            {
                await statusHistoryRepository.AddAsync(
                    new TicketStatusHistory(
                        ticket.TicketId, TicketStatusDimension.SlaState, (byte)previousState, (byte)ticket.SlaState,
                        actorEmployeeId, actorIsSystem: true,
                        note: $"SLA clock started at request-type classification ({nowUtc:O}); the ticket had no priority before.",
                        correlationId, nowUtc),
                    cancellationToken);
            }

            return "StartedAtClassification";
        }

        return (await slaDueDateService.ReapplyPolicyForRequestTypeAsync(ticket, actorEmployeeId, correlationId, cancellationToken)).ToString();
    }

    private static RequestTypeResolution Fail(RequestTypeResolutionFailure failure, string detail) =>
        new(null, null, failure, detail);
}

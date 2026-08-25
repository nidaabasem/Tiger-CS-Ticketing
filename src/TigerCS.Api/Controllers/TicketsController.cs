using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// Items 3–8 of this increment's scope, plus a later business-rule change:
/// a single ticket-creation path for every IntakeRecord. Customer
/// information from CRM, PACT, or Tasleeh is attached when available
/// (<see cref="CustomerLookupController"/>); lack of a match never blocks
/// <see cref="Create"/> — the only thing every ticket requires is a valid
/// Ticket Category. Scoped to CS Agent/CS Supervisor only
/// (PolicyNames.CustomerVerification) — same rationale as
/// VerificationSessionsController/CrmController: the Solution-Analysis.md
/// §4.1 permission matrix grants ticket-Create to exactly these two roles.
/// </summary>
[ApiController]
[Route("api/tickets")]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[Tags(OpenApiTags.Tickets)]
public class TicketsController(
    TicketCreationAppService ticketCreationAppService,
    TicketQueryAppService ticketQueryAppService,
    TicketAssignmentAppService ticketAssignmentAppService,
    TicketLifecycleAppService ticketLifecycleAppService,
    TicketNoteAppService ticketNoteAppService,
    TicketReconciliationAppService ticketReconciliationAppService) : ControllerBase
{
    /// <summary>Create a ticket from an IntakeRecord. CS Agent/CS Supervisor only.</summary>
    /// <remarks>
    /// Create a ticket from an IntakeRecord. Customer information from CRM,
    /// PACT, or Tasleeh is attached when available (see
    /// <c>GET /api/intake-records/{intakeRecordId}/customer-lookup</c>);
    /// lack of a match does not prevent ticket creation. Ticket Category is
    /// required for every ticket. Scoped to CS Agent/CS Supervisor
    /// (Solution-Analysis.md §4.1's Create column) — layered on top of the
    /// class-level AuthenticatedStaff policy, not a replacement for it.
    /// </remarks>
    /// <param name="request">The intake record to promote, plus the matched unit/contact (if the agent selected one), category, priority, and summary.</param>
    /// <response code="201">The created ticket.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">The intake record, unit reference, contact reference, category, priority, or the category's routed department was not found (or the department is inactive).</response>
    /// <response code="409">The intake record was already promoted to a ticket, or a ticket-number collision occurred — retry.</response>
    /// <response code="422">UnitReferenceId and ContactReferenceId were not both supplied or both omitted.</response>
    [HttpPost]
    [Authorize(Policy = PolicyNames.CustomerVerification)]
    [ProducesResponseType<TicketResponseDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] CreateTicketRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketCreationAppService.CreateAsync(employeeId.Value, request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>The ticket queue — a filtered, sorted, paged list of tickets visible to the caller.</summary>
    /// <remarks>
    /// MVP-API-Contracts.md §3.2. Department visibility is resolved
    /// server-side from the caller's roles and department assignments
    /// (TicketQueryAppService), never from a client-supplied department
    /// filter alone: <c>departmentId</c> narrows what the caller may already
    /// see, it does not widen it.
    /// </remarks>
    /// <param name="request">
    /// Filtering, sorting, and paging. All filters are optional and combine
    /// with AND. <c>ticketStatus</c> is one of Open, InProgress,
    /// PendingCustomer, PendingThirdParty, Resolved, Closed;
    /// <c>verificationStatus</c> one of Unverified, PendingCrmVerification,
    /// Verified; <c>priorityId</c> 1=Critical, 2=High, 3=Medium, 4=Low.
    /// </param>
    /// <response code="200">A page of ticket summaries, with the total matching count.</response>
    [HttpGet]
    [ProducesResponseType<TicketListResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetQueue([FromQuery] TicketListRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketQueryAppService.GetQueueAsync(employeeId.Value, GetRoles(), request, cancellationToken);
        return Ok(result);
    }

    /// <summary>Full detail for one ticket.</summary>
    /// <remarks>
    /// MVP-API-Contracts.md §3.3. A ticket created with no customer match is
    /// returned with verificationStatus Unverified and a null unit/contact —
    /// never fabricated as Verified. <c>rowVersion</c> from this response is
    /// what the assignment, transfer, status, resolution, close, and
    /// reconciliation calls expect back.
    /// </remarks>
    /// <param name="ticketId">The ticket to fetch.</param>
    /// <response code="200">The ticket.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    [HttpGet("{ticketId:long}")]
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDetail(long ticketId, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketQueryAppService.GetDetailAsync(employeeId.Value, GetRoles(), ticketId, cancellationToken);
        return result.Outcome switch
        {
            TicketQueryOutcome.Success => Ok(result.Response),
            TicketQueryOutcome.Forbidden => Forbid(),
            _ => NotFound()
        };
    }

    /// <summary>Assign a ticket to an employee — self-claim or reassign.</summary>
    /// <remarks>
    /// MVP-API-Contracts.md §3.5. Both self-assignment and cross-department
    /// reach are validated inside TicketAssignmentAppService, never trusted
    /// from the request alone.
    /// </remarks>
    /// <param name="ticketId">The ticket to assign.</param>
    /// <param name="request">The employee to assign it to, and the ticket's current rowVersion.</param>
    /// <response code="200">The updated ticket.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    /// <response code="409">rowVersion did not match — another request already modified this ticket. Reload it and retry.</response>
    /// <response code="422">The requested change is not valid for this ticket's current state.</response>
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [HttpPost("{ticketId:long}/assignment")]
    [Tags(OpenApiTags.Assignment)]
    public async Task<IActionResult> Assign(long ticketId, [FromBody] AssignTicketRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketAssignmentAppService.AssignAsync(employeeId.Value, GetRoles(), ticketId, request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>Transfer a ticket to another department.</summary>
    /// <remarks>
    /// MVP-API-Contracts.md §3.6. Clears the current owner — the receiving
    /// department must explicitly claim or assign it.
    /// </remarks>
    /// <param name="ticketId">The ticket to transfer.</param>
    /// <param name="request">The target department, the reason, and the ticket's current rowVersion.</param>
    /// <response code="200">The updated ticket.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    /// <response code="409">rowVersion did not match — another request already modified this ticket. Reload it and retry.</response>
    /// <response code="422">The requested change is not valid for this ticket's current state.</response>
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [HttpPost("{ticketId:long}/transfer")]
    [Tags(OpenApiTags.Transfer)]
    public async Task<IActionResult> Transfer(long ticketId, [FromBody] TransferTicketRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketAssignmentAppService.TransferAsync(employeeId.Value, GetRoles(), ticketId, request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>Change a ticket's working status.</summary>
    /// <remarks>
    /// MVP-API-Contracts.md §3.7 — the "work" sub-machine only
    /// (Open to InProgress, InProgress to and from PendingCustomer/
    /// PendingThirdParty). Resolved and Closed are reached only via
    /// <see cref="Resolve"/> and <see cref="Close"/>.
    /// </remarks>
    /// <param name="ticketId">The ticket to update.</param>
    /// <param name="request">The new status (Open, InProgress, PendingCustomer, or PendingThirdParty) and the ticket's current rowVersion.</param>
    /// <response code="200">The updated ticket.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    /// <response code="409">rowVersion did not match — another request already modified this ticket. Reload it and retry.</response>
    /// <response code="422">The requested change is not valid for this ticket's current state.</response>
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [HttpPost("{ticketId:long}/status")]
    [Tags(OpenApiTags.TicketLifecycle)]
    public async Task<IActionResult> ChangeStatus(long ticketId, [FromBody] ChangeStatusRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketLifecycleAppService.ChangeStatusAsync(employeeId.Value, GetRoles(), ticketId, request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>Resolve a ticket. Department Employee/Department Head only.</summary>
    /// <remarks>MVP-API-Contracts.md §3.9, ISSUE-022's approved Resolve/Close split.</remarks>
    /// <param name="ticketId">The ticket to resolve.</param>
    /// <param name="request">
    /// The outcome — one of Resolved, Cancelled, Rejected, Duplicate — the
    /// resolution note, an optional reason code, the duplicate target when
    /// the outcome is Duplicate, and the ticket's current rowVersion.
    /// </param>
    /// <response code="200">The resolved ticket.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    /// <response code="409">rowVersion did not match — another request already modified this ticket. Reload it and retry.</response>
    /// <response code="422">The requested change is not valid for this ticket's current state.</response>
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [HttpPost("{ticketId:long}/resolution")]
    [Tags(OpenApiTags.TicketLifecycle)]
    public async Task<IActionResult> Resolve(long ticketId, [FromBody] ResolveTicketRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketLifecycleAppService.ResolveAsync(employeeId.Value, GetRoles(), ticketId, request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>Close a resolved ticket. CS Agent/CS Supervisor/CS Manager only.</summary>
    /// <remarks>MVP-API-Contracts.md §3.10, ISSUE-022's approved Resolve/Close split.</remarks>
    /// <param name="ticketId">The ticket to close.</param>
    /// <param name="request">The ticket's current rowVersion.</param>
    /// <response code="200">The closed ticket.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    /// <response code="409">rowVersion did not match — another request already modified this ticket. Reload it and retry.</response>
    /// <response code="422">The requested change is not valid for this ticket's current state.</response>
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [HttpPost("{ticketId:long}/close")]
    [Tags(OpenApiTags.TicketLifecycle)]
    public async Task<IActionResult> Close(long ticketId, [FromBody] CloseTicketRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketLifecycleAppService.CloseAsync(employeeId.Value, GetRoles(), ticketId, request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>Link a confirmed CRM match onto a ticket that did not have one at creation. CS Agent/CS Supervisor only.</summary>
    /// <remarks>
    /// See TicketReconciliationAppService's remarks. Scoped to CS Agent/CS
    /// Supervisor — the same actors who own verification-session creation.
    /// </remarks>
    /// <param name="ticketId">The not-yet-verified ticket to reconcile.</param>
    /// <param name="request">The confirmed verification session to consume, and the ticket's current rowVersion.</param>
    /// <response code="200">The reconciled ticket, now verificationStatus Verified.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    /// <response code="409">rowVersion did not match — another request already modified this ticket. Reload it and retry.</response>
    /// <response code="422">The requested change is not valid for this ticket's current state.</response>
    [ProducesResponseType<TicketDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [HttpPost("{ticketId:long}/reconciliation")]
    [Authorize(Policy = PolicyNames.CustomerVerification)]
    [Tags(OpenApiTags.CrmReconciliation)]
    public async Task<IActionResult> Reconcile(long ticketId, [FromBody] ReconcileTicketRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketReconciliationAppService.ReconcileAsync(employeeId.Value, ticketId, request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>Add a note to a ticket.</summary>
    /// <remarks>MVP-API-Contracts.md §4.1.</remarks>
    /// <param name="ticketId">The ticket to annotate.</param>
    /// <param name="request">The note text.</param>
    /// <response code="201">The created note.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    [HttpPost("{ticketId:long}/notes")]
    [Tags(OpenApiTags.Notes)]
    [ProducesResponseType<TicketNoteResponseDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddNote(long ticketId, [FromBody] CreateNoteRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketNoteAppService.AddNoteAsync(employeeId.Value, GetRoles(), ticketId, request, cancellationToken);
        return result.Outcome switch
        {
            NoteOutcome.Success => Created($"/api/tickets/{ticketId}/notes", result.Response),
            NoteOutcome.Forbidden => Forbid(),
            _ => NotFound()
        };
    }

    /// <summary>List a ticket's notes, newest page first.</summary>
    /// <remarks>MVP-API-Contracts.md §4.2.</remarks>
    /// <param name="ticketId">The ticket whose notes to list.</param>
    /// <param name="page">1-based page number. 0 is treated as 1.</param>
    /// <param name="pageSize">Page size. 0 is treated as 50.</param>
    /// <response code="200">A page of notes, with the total count.</response>
    /// <response code="404">No such ticket, or it is not visible to the caller.</response>
    [HttpGet("{ticketId:long}/notes")]
    [Tags(OpenApiTags.Notes)]
    [ProducesResponseType<TicketNoteListResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListNotes(long ticketId, [FromQuery] int page, [FromQuery] int pageSize, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketNoteAppService.ListNotesAsync(
            employeeId.Value, GetRoles(), ticketId, page == 0 ? 1 : page, pageSize == 0 ? 50 : pageSize, cancellationToken);
        return result.Outcome switch
        {
            TicketQueryOutcome.Success => Ok(result.Response),
            TicketQueryOutcome.Forbidden => Forbid(),
            _ => NotFound()
        };
    }

    private IActionResult ToActionResult(TicketCreationResult result) => result.Outcome switch
    {
        TicketCreationOutcome.Success =>
            Created($"/api/tickets/{result.Response!.TicketId}", result.Response),

        TicketCreationOutcome.IntakeRecordNotFound => Problem(
            type: "https://tigercs.internal/problems/intake-record-not-found",
            title: "Intake record not found",
            statusCode: StatusCodes.Status404NotFound),

        TicketCreationOutcome.IntakeRecordAlreadyLinked => Problem(
            type: "https://tigercs.internal/problems/intake-record-already-linked",
            title: "Intake record already promoted",
            detail: "This IntakeRecord is already linked to a ticket.",
            statusCode: StatusCodes.Status409Conflict),

        TicketCreationOutcome.UnitOrContactReferenceMismatch => Problem(
            type: "https://tigercs.internal/problems/unit-or-contact-reference-mismatch",
            title: "UnitReferenceId and ContactReferenceId mismatch",
            detail: "UnitReferenceId and ContactReferenceId must both be supplied or both be omitted.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketCreationOutcome.UnitReferenceNotFound => Problem(
            type: "https://tigercs.internal/problems/unit-reference-not-found",
            title: "Unit reference not found",
            statusCode: StatusCodes.Status404NotFound),

        TicketCreationOutcome.ContactReferenceNotFound => Problem(
            type: "https://tigercs.internal/problems/contact-reference-not-found",
            title: "Contact reference not found",
            statusCode: StatusCodes.Status404NotFound),

        TicketCreationOutcome.CategoryNotFound => Problem(
            type: "https://tigercs.internal/problems/category-not-found",
            title: "Category not found",
            statusCode: StatusCodes.Status404NotFound),

        TicketCreationOutcome.CategoryDepartmentMismatch => Problem(
            type: "https://tigercs.internal/problems/category-department-mismatch",
            title: "Category does not belong to the Intake department",
            detail: "The selected category does not belong to the Intake department.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketCreationOutcome.PriorityNotFound => Problem(
            type: "https://tigercs.internal/problems/priority-not-found",
            title: "Priority not found",
            statusCode: StatusCodes.Status404NotFound),

        TicketCreationOutcome.DepartmentInactive => Problem(
            type: "https://tigercs.internal/problems/department-inactive",
            title: "Routed department is inactive",
            detail: "This Category routes to a Department that is missing or deactivated.",
            statusCode: StatusCodes.Status404NotFound),

        TicketCreationOutcome.TicketNumberCollision => Problem(
            type: "https://tigercs.internal/problems/ticket-number-collision",
            title: "Ticket number collision",
            detail: "A concurrent request generated the same ticket number for this department/day — retry the request.",
            statusCode: StatusCodes.Status409Conflict),

        _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
    };

    private IActionResult ToActionResult(TicketMutationResult result) => result.Outcome switch
    {
        TicketMutationOutcome.Success => Ok(result.Response),

        TicketMutationOutcome.NotFound => NotFound(),

        TicketMutationOutcome.Forbidden => Forbid(),

        TicketMutationOutcome.ConcurrencyConflict => Problem(
            type: "https://tigercs.internal/problems/ticket-concurrently-modified",
            title: "Ticket concurrently modified",
            detail: "Another request already modified this ticket — reload it and retry.",
            statusCode: StatusCodes.Status409Conflict),

        // Closed-ticket immutability (PR correction): Assign/Transfer/
        // ChangeStatus/Resolve/Close all reject once TicketStatus is
        // Closed — no database changes are made for this outcome.
        TicketMutationOutcome.TicketClosed => Problem(
            type: "https://tigercs.internal/problems/ticket-closed",
            title: "Ticket is closed",
            detail: "This ticket is Closed and no longer accepts assignment, transfer, status changes, resolution, or closing.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.EmployeeNotInDepartment => Problem(
            type: "https://tigercs.internal/problems/employee-not-in-department",
            title: "Employee not in department",
            detail: "AssignedEmployeeId is not an active member of this ticket's current department.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.TargetDepartmentInactive => Problem(
            type: "https://tigercs.internal/problems/department-inactive",
            title: "Target department is inactive",
            statusCode: StatusCodes.Status404NotFound),

        TicketMutationOutcome.AlreadyInTargetDepartment => Problem(
            type: "https://tigercs.internal/problems/already-in-target-department",
            title: "Ticket is already in the target department",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.InvalidStatusTransition => Problem(
            type: "https://tigercs.internal/problems/invalid-status-transition",
            title: "Invalid status transition",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.TicketNotAssigned => Problem(
            type: "https://tigercs.internal/problems/ticket-not-assigned",
            title: "Ticket has no current owner",
            detail: "Assign the ticket before starting work on it.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.NotEligibleForResolution => Problem(
            type: "https://tigercs.internal/problems/not-eligible-for-resolution",
            title: "Ticket is not eligible for resolution",
            detail: "Resolve is only valid from InProgress, PendingCustomer, or PendingThirdParty.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.NotYetResolved => Problem(
            type: "https://tigercs.internal/problems/not-yet-resolved",
            title: "Ticket has not been resolved yet",
            statusCode: StatusCodes.Status409Conflict),

        TicketMutationOutcome.DuplicateChainNotAllowed => Problem(
            type: "https://tigercs.internal/problems/duplicate-chain-not-allowed",
            title: "Invalid duplicate target",
            detail: "DuplicateOfTicketId must reference an existing, non-duplicate ticket.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.ReconciliationUnitMismatch => Problem(
            type: "https://tigercs.internal/problems/reconciliation-unit-mismatch",
            title: "Reconciliation unit mismatch",
            detail: "The verification session's unit does not match this ticket's originating raw unit number.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.AlreadyVerified => Problem(
            type: "https://tigercs.internal/problems/ticket-already-verified",
            title: "Ticket is already verified",
            detail: "This ticket already has a linked unit/contact — there is nothing left to reconcile.",
            statusCode: StatusCodes.Status409Conflict),

        TicketMutationOutcome.VerificationSessionNotFound => Problem(
            type: "https://tigercs.internal/problems/verification-session-not-found",
            title: "Verification session not found",
            statusCode: StatusCodes.Status404NotFound),

        TicketMutationOutcome.VerificationSessionForbidden => Forbid(),

        TicketMutationOutcome.VerificationSessionNotConfirmed => Problem(
            type: "https://tigercs.internal/problems/verification-session-not-confirmed",
            title: "Verification session not confirmed",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketMutationOutcome.VerificationSessionAlreadyConsumed => Problem(
            type: "https://tigercs.internal/problems/verification-session-already-consumed",
            title: "Verification session already consumed",
            statusCode: StatusCodes.Status409Conflict),

        TicketMutationOutcome.VerificationSessionExpired => Problem(
            type: "https://tigercs.internal/problems/verification-session-expired",
            title: "Verification session expired",
            statusCode: StatusCodes.Status410Gone),

        _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
    };

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }

    /// <summary>The caller's roles, from the JWT's own role claims — never client-supplied, and the app-service layer's sole source of role-based branching (Solution-Analysis.md §4.1's finer-grained per-action role sets, see TicketRoleSets).</summary>
    private IReadOnlyCollection<string> GetRoles() =>
        User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
}

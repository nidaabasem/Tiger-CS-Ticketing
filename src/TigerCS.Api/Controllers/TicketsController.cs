using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// Items 3–8 of this increment's scope. Two creation paths, matching
/// TicketCreationAppService's own two methods:
/// <list type="bullet">
/// <item><see cref="Create"/> — the normal path (FR-CH-01/FR-VER-02):
/// a unit-related request, from an already-confirmed VerificationSession.</item>
/// <item><see cref="CreateProvisional"/> — ISSUE-006's approved fallback:
/// Critical/High proceeds immediately while the CRM is unreachable;
/// Medium/Low is queued instead of rejected outright (200, not an error).</item>
/// </list>
/// Scoped to CS Agent/CS Supervisor only (PolicyNames.CustomerVerification) —
/// same rationale as VerificationSessionsController/CrmController: the
/// Solution-Analysis.md §4.1 permission matrix grants ticket-Create to
/// exactly these two roles.
/// </summary>
[ApiController]
[Route("api/tickets")]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
public class TicketsController(
    TicketCreationAppService ticketCreationAppService,
    TicketQueryAppService ticketQueryAppService,
    TicketAssignmentAppService ticketAssignmentAppService,
    TicketLifecycleAppService ticketLifecycleAppService,
    TicketNoteAppService ticketNoteAppService,
    TicketReconciliationAppService ticketReconciliationAppService) : ControllerBase
{
    /// <summary>Scoped to CS Agent/CS Supervisor only (Solution-Analysis.md §4.1's Create column) — layered on top of the class-level AuthenticatedStaff policy, not a replacement for it.</summary>
    [HttpPost]
    [Authorize(Policy = PolicyNames.CustomerVerification)]
    public async Task<IActionResult> Create([FromBody] CreateTicketFromVerificationRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketCreationAppService.CreateFromVerificationSessionAsync(employeeId.Value, request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>ISSUE-006 (approved as recommended, Management-Decisions.md) — see TicketCreationAppService.CreateProvisionalAsync's remarks.</summary>
    [HttpPost("provisional")]
    [Authorize(Policy = PolicyNames.CustomerVerification)]
    public async Task<IActionResult> CreateProvisional(
        [FromBody] CreateProvisionalTicketRequestDto request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketCreationAppService.CreateProvisionalAsync(employeeId.Value, request, cancellationToken);
        return ToActionResult(result);
    }

    /// <summary>MVP-API-Contracts.md §3.2 — the ticket queue. Department visibility is resolved server-side (TicketQueryAppService), never from a client-supplied department filter alone.</summary>
    [HttpGet]
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

    /// <summary>MVP-API-Contracts.md §3.3 — full ticket detail, including PendingCrmVerification/null Unit-Contact for a not-yet-reconciled provisional ticket (never fabricated as Verified).</summary>
    [HttpGet("{ticketId:long}")]
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

    /// <summary>MVP-API-Contracts.md §3.5 — self-claim or reassign; both self-assignment and cross-department reach are validated inside TicketAssignmentAppService, never trusted from the request alone.</summary>
    [HttpPost("{ticketId:long}/assignment")]
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

    /// <summary>MVP-API-Contracts.md §3.6 — clears the current owner; the receiving department must explicitly claim/assign it.</summary>
    [HttpPost("{ticketId:long}/transfer")]
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

    /// <summary>MVP-API-Contracts.md §3.7 — the "work" sub-machine only (Open→InProgress, InProgress↔Pending*); Resolved/Closed are reached only via <see cref="Resolve"/>/<see cref="Close"/>.</summary>
    [HttpPost("{ticketId:long}/status")]
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

    /// <summary>MVP-API-Contracts.md §3.9 — ISSUE-022: Department Employee/Head only.</summary>
    [HttpPost("{ticketId:long}/resolution")]
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

    /// <summary>MVP-API-Contracts.md §3.10 — ISSUE-022: CS Agent/CS Supervisor/CS Manager only.</summary>
    [HttpPost("{ticketId:long}/close")]
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

    /// <summary>This increment's item 6 — see TicketReconciliationAppService's remarks; scoped to CS Agent/CS Supervisor, the same actors who own verification-session creation.</summary>
    [HttpPost("{ticketId:long}/reconciliation")]
    [Authorize(Policy = PolicyNames.CustomerVerification)]
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

    /// <summary>MVP-API-Contracts.md §4.1.</summary>
    [HttpPost("{ticketId:long}/notes")]
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

    /// <summary>MVP-API-Contracts.md §4.2.</summary>
    [HttpGet("{ticketId:long}/notes")]
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

        // Not an error — ISSUE-006's approved "Medium/Low remains queued"
        // outcome. 200, carrying the updated IntakeRecord, not a ticket.
        TicketCreationOutcome.QueuedPendingVerification => Ok(result.QueuedIntakeRecord),

        TicketCreationOutcome.IntakeRecordNotFound => Problem(
            type: "https://tigercs.internal/problems/intake-record-not-found",
            title: "Intake record not found",
            statusCode: StatusCodes.Status404NotFound),

        TicketCreationOutcome.IntakeRecordAlreadyLinked => Problem(
            type: "https://tigercs.internal/problems/intake-record-already-linked",
            title: "Intake record already promoted",
            detail: "This IntakeRecord is already linked to a ticket.",
            statusCode: StatusCodes.Status409Conflict),

        TicketCreationOutcome.IntakeRecordNotUnitRelated => Problem(
            type: "https://tigercs.internal/problems/intake-record-not-unit-related",
            title: "Intake record is not unit-related",
            detail: "A non-unit-related IntakeRecord cannot be promoted to a ticket in this increment.",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketCreationOutcome.VerificationSessionNotFound => Problem(
            type: "https://tigercs.internal/problems/verification-session-not-found",
            title: "Verification session not found",
            statusCode: StatusCodes.Status404NotFound),

        TicketCreationOutcome.VerificationSessionForbidden => Forbid(),

        TicketCreationOutcome.VerificationSessionNotConfirmed => Problem(
            type: "https://tigercs.internal/problems/verification-session-not-confirmed",
            title: "Verification session not confirmed",
            statusCode: StatusCodes.Status422UnprocessableEntity),

        TicketCreationOutcome.VerificationSessionAlreadyConsumed => Problem(
            type: "https://tigercs.internal/problems/verification-session-already-consumed",
            title: "Verification session already consumed",
            statusCode: StatusCodes.Status409Conflict),

        TicketCreationOutcome.VerificationSessionExpired => Problem(
            type: "https://tigercs.internal/problems/verification-session-expired",
            title: "Verification session expired",
            statusCode: StatusCodes.Status410Gone),

        TicketCreationOutcome.CategoryNotFound => Problem(
            type: "https://tigercs.internal/problems/category-not-found",
            title: "Category not found",
            statusCode: StatusCodes.Status404NotFound),

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

        TicketMutationOutcome.NotPendingCrmVerification => Problem(
            type: "https://tigercs.internal/problems/not-pending-crm-verification",
            title: "Ticket is not pending CRM verification",
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

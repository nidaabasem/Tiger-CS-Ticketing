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
[Authorize(Policy = PolicyNames.CustomerVerification)]
public class TicketsController(TicketCreationAppService ticketCreationAppService) : ControllerBase
{
    [HttpPost]
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

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }
}

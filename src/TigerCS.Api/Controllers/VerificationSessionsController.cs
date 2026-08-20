using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// <b>A Tiger CS Ticketing endpoint — not a CRM endpoint.</b> Customer/
/// requester verification (selecting the requester, recording the
/// verification method/result, deciding whether ticket creation is
/// allowed, the immutable verification-time snapshot, and the audit/
/// authorization/expiry rules) is Tiger CS Ticketing's own business logic;
/// Tiger CRM is consulted only as a read-only data source via
/// <c>CrmUnitLookupAppService</c>/<c>ICrmGateway</c> before this endpoint is
/// ever called. See <c>VerificationSessionAppService</c>'s remarks for the
/// full ownership boundary — that is where this controller's logic
/// actually lives; this class stays a thin HTTP adapter over it, same as
/// every other controller in this codebase.
///
/// <para>
/// MVP-API-Contracts.md §2.4, simplified per MVP-Implementation-Backlog.md
/// §0.2/S-07 into a single combined create+select+confirm call — see
/// VerificationSessionAppService's remarks for the full rationale. Scoped to
/// CS Agent/CS Supervisor only (PolicyNames.CustomerVerification) — see
/// CrmController's remarks for why this is narrower than the contract's
/// literal "Agent and above" wording.
/// </para>
/// </summary>
[ApiController]
[Route("api/verification-sessions")]
[Authorize(Policy = PolicyNames.CustomerVerification)]
public class VerificationSessionsController(VerificationSessionAppService verificationSessionAppService) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateVerificationSessionRequestDto request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!request.ConfirmedVerbally)
        {
            ModelState.AddModelError(nameof(request.ConfirmedVerbally), "ConfirmedVerbally must be true.");
            return ValidationProblem(ModelState);
        }

        var agentEmployeeId = GetEmployeeId();
        if (agentEmployeeId is null)
        {
            return Unauthorized();
        }

        var result = await verificationSessionAppService.CreateAndConfirmAsync(
            agentEmployeeId.Value, request, idempotencyKey, cancellationToken);

        return result.Outcome switch
        {
            VerificationSessionOutcome.Success =>
                Created($"/api/verification-sessions/{result.Response!.VerificationSessionId}", result.Response),
            VerificationSessionOutcome.UnitOrContactNotFound => Problem(
                type: "https://tigercs.internal/problems/unit-or-contact-not-found",
                title: "Unit or contact not found",
                detail: "UnitReferenceId/ContactReferenceId must reference an already-looked-up unit and one of its contacts.",
                statusCode: StatusCodes.Status404NotFound),
            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    /// <summary>Owner-only (single-agent ownership, MVP-ERD.md §2.24) — used to resume state or hand a confirmed session's data to later ticket creation (out of scope this phase).</summary>
    [HttpGet("{verificationSessionId:guid}")]
    public async Task<IActionResult> Get(Guid verificationSessionId, CancellationToken cancellationToken)
    {
        var callerEmployeeId = GetEmployeeId();
        if (callerEmployeeId is null)
        {
            return Unauthorized();
        }

        var result = await verificationSessionAppService.GetAsync(verificationSessionId, callerEmployeeId.Value, cancellationToken);

        return result.Outcome switch
        {
            VerificationSessionOutcome.Success => Ok(result.Response),
            VerificationSessionOutcome.NotFound => NotFound(),
            VerificationSessionOutcome.Forbidden => Forbid(),
            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// Item 1 of this increment's scope: capture every customer interaction as
/// an IntakeRecord before any verification is attempted (MVP-ERD.md §2.9),
/// so no request — unit-related or not (item 2) — is ever silently lost.
/// Scoped to CS Agent/CS Supervisor only (PolicyNames.CustomerVerification),
/// same rationale as VerificationSessionsController/CrmController: this is
/// the first step of the same verify-then-create sequence those endpoints
/// gate, and no role that cannot go on to create a ticket has a documented
/// need to record an intake either.
/// </summary>
[ApiController]
[Route("api/intake-records")]
[Authorize(Policy = PolicyNames.CustomerVerification)]
public class IntakeRecordsController(IntakeRecordAppService intakeRecordAppService) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateIntakeRecordRequestDto request, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<Channel>(request.ChannelId, ignoreCase: false, out _))
        {
            ModelState.AddModelError(
                nameof(request.ChannelId), $"ChannelId must be one of: {string.Join(", ", Enum.GetNames<Channel>())}.");
            return ValidationProblem(ModelState);
        }

        if (request.IsUnitRelated && string.IsNullOrWhiteSpace(request.RawUnitNumberEntered))
        {
            ModelState.AddModelError(nameof(request.RawUnitNumberEntered), "Required when IsUnitRelated is true.");
            return ValidationProblem(ModelState);
        }

        if (!request.IsUnitRelated && request.RawUnitNumberEntered is not null)
        {
            ModelState.AddModelError(nameof(request.RawUnitNumberEntered), "Must be omitted when IsUnitRelated is false.");
            return ValidationProblem(ModelState);
        }

        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await intakeRecordAppService.CreateAsync(employeeId.Value, request, cancellationToken);
        return Created($"/api/intake-records/{result.Response!.IntakeRecordId}", result.Response);
    }

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

[ApiController]
[Route("api/users")]
[Tags(OpenApiTags.Users)]
public class UsersController(
    UserProfileAppService userProfileAppService,
    UserActivationAppService userActivationAppService)
    : ControllerBase
{
    /// <summary>The signed-in user's own profile.</summary>
    /// <remarks>
    /// Resolved from the access token's subject claim, never from a
    /// client-supplied id. MVP-API-Contracts.md §1.3.
    /// </remarks>
    /// <response code="200">The caller's employee id, display name, roles, and department memberships.</response>
    [HttpGet("me")]
    [ProducesResponseType<CurrentUserResponseDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (idValue is null || !Guid.TryParse(idValue, out var employeeId))
        {
            return Unauthorized();
        }

        var profile = await userProfileAppService.GetCurrentUserAsync(employeeId, cancellationToken);
        return profile is null ? Unauthorized() : Ok(profile);
    }

    /// <summary>Activate or deactivate a user. System Administrator only.</summary>
    /// <remarks>MVP-API-Contracts.md §1.6.</remarks>
    /// <param name="employeeId">The employee to activate or deactivate.</param>
    /// <param name="request">The desired activation state, and an optional reason.</param>
    /// <response code="200">Activation updated. Returns the new state and how many of that user's open tickets were affected.</response>
    /// <response code="400">The request body was malformed.</response>
    /// <response code="404">No such employee.</response>
    /// <response code="409">Refused: this is the last active System Administrator.</response>
    [HttpPatch("{employeeId:guid}/activation")]
    [Authorize(Policy = PolicyNames.SystemAdministrator)]
    [ProducesResponseType<ActivationResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetActivation(
        Guid employeeId, [FromBody] ActivationRequestDto request, CancellationToken cancellationToken)
    {
        var result = await userActivationAppService.SetActivationAsync(employeeId, request, cancellationToken);

        return result.Outcome switch
        {
            ActivationOutcome.Success => Ok(result.Response),
            ActivationOutcome.NotFound => NotFound(),
            ActivationOutcome.LastActiveAdministrator => Problem(
                type: "https://tigercs.internal/problems/last-admin",
                title: "Cannot deactivate the last active System Administrator",
                statusCode: StatusCodes.Status409Conflict),
            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }
}

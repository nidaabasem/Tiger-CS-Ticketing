using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

[ApiController]
[Route("api/users")]
public class UsersController(
    UserProfileAppService userProfileAppService,
    UserActivationAppService userActivationAppService)
    : ControllerBase
{
    /// <summary>MVP-API-Contracts.md §1.3.</summary>
    [HttpGet("me")]
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

    /// <summary>MVP-API-Contracts.md §1.6.</summary>
    [HttpPatch("{employeeId:guid}/activation")]
    [Authorize(Policy = PolicyNames.SystemAdministrator)]
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

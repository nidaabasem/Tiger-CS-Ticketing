using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Services;

namespace TigerCS.Api.Controllers;

/// <summary>MVP-API-Contracts.md §1.1/§1.2.</summary>
[ApiController]
[Route("api/auth")]
public class AuthController(AuthenticationAppService authenticationAppService) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequestDto request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return ValidationProblem();
        }

        var result = await authenticationAppService.LoginAsync(request, cancellationToken);

        return result.Outcome switch
        {
            LoginOutcome.Success => Ok(result.Response),
            LoginOutcome.Locked => Problem(
                type: "https://tigercs.internal/problems/account-locked",
                title: "Account locked",
                statusCode: StatusCodes.Status423Locked),
            _ => Problem(
                type: "https://tigercs.internal/problems/invalid-credentials",
                title: "Invalid credentials",
                statusCode: StatusCodes.Status401Unauthorized)
        };
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (idValue is not null && Guid.TryParse(idValue, out var employeeId))
        {
            await authenticationAppService.LogoutAsync(employeeId, cancellationToken);
        }

        return NoContent();
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Services;

namespace TigerCS.Api.Controllers;

/// <summary>MVP-API-Contracts.md §1.1/§1.2.</summary>
[ApiController]
[Route("api/auth")]
[Tags(OpenApiTags.Authentication)]
public class AuthController(AuthenticationAppService authenticationAppService) : ControllerBase
{
    /// <summary>Sign in and obtain a JWT access token.</summary>
    /// <remarks>
    /// The only endpoint besides <c>GET /health</c> that does not require an
    /// access token. Copy <c>accessToken</c> from a 200 response into
    /// Swagger UI's <b>Authorize</b> box to call the rest of the API.
    /// </remarks>
    /// <param name="request">Username and password. Both are required and must be non-blank.</param>
    /// <response code="200">Signed in. Returns the access token, its UTC expiry, and the caller's identity, roles, and primary department.</response>
    /// <response code="400">Username or password was missing or blank.</response>
    /// <response code="401">The username/password combination was not accepted.</response>
    /// <response code="423">The account is locked.</response>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<LoginResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status423Locked)]
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

    /// <summary>Sign out the current user.</summary>
    /// <remarks>Succeeds with 204 whether or not the token carried a resolvable employee id.</remarks>
    /// <response code="204">Signed out.</response>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
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

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// The operational Dashboard (Customer Workspace phase): KPI counts and the
/// Tickets Requiring Attention list. One read-only aggregate — every number
/// is computed server-side over the caller's own visible-department scope,
/// resolved from their roles/department membership exactly as the ticket
/// queue resolves it (<see cref="TicketsController.GetQueue"/>), never from
/// anything client-supplied. Open to all authenticated staff: the response
/// only ever summarizes tickets the caller could already list.
/// </summary>
[ApiController]
[Route("api/dashboard")]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[Tags(OpenApiTags.Dashboard)]
public class DashboardController(DashboardAppService dashboardAppService) : ControllerBase
{
    /// <summary>The caller's operational dashboard: KPI counts and Tickets Requiring Attention, scoped to their visible departments.</summary>
    /// <response code="200">The dashboard summary. Counts cover only tickets the caller may view.</response>
    [HttpGet]
    [ProducesResponseType<DashboardSummaryDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary(CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await dashboardAppService.GetSummaryAsync(employeeId.Value, GetRoles(), cancellationToken);
        return Ok(result);
    }

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }

    private IReadOnlyCollection<string> GetRoles() =>
        User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
}

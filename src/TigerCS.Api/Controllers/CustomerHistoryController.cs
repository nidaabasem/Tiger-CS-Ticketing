using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// Customer → Previous Ticket History for a CRM-verified customer, keyed by
/// the exact <c>CrmBuyerCustomerId</c> the agent selected on the New Ticket
/// wizard's CRM Buyer Lookup step (<c>GET /api/crm/buyers</c>) — never by
/// phone number, since one phone number may match more than one CRM
/// customer. Sourced entirely from the existing Tickets table; this
/// controller never calls CRM (<see cref="CrmController"/>) itself, so
/// history remains available even when CRM is offline. Department
/// visibility is resolved server-side from the caller's own roles/
/// department membership, exactly as the ticket queue does
/// (<see cref="TicketsController.GetQueue"/>) — a caller who happens to know
/// a valid CrmBuyerCustomerId never sees a ticket outside their own visible
/// departments.
/// </summary>
[ApiController]
[Route("api/customers")]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[Tags(OpenApiTags.CustomerHistory)]
public class CustomerHistoryController(CustomerHistoryAppService customerHistoryAppService) : ControllerBase
{
    /// <summary>Previous tickets for a CRM-verified customer, newest first.</summary>
    /// <remarks>
    /// Used by the New Ticket wizard's Step 3 preview, right after the agent
    /// selects a CRM Buyer/unit — always the selected customer, never the
    /// first raw phone-search result. Never a live CRM call.
    /// </remarks>
    /// <param name="crmCustomerId">The CRM Buyer's customer id, as selected from <c>GET /api/crm/buyers</c>.</param>
    /// <param name="limit">Maximum tickets to return, newest first. Defaults to 5; capped at 50.</param>
    /// <response code="200">The customer's ticket-count summary and its most recent tickets (possibly empty).</response>
    [HttpGet("crm/{crmCustomerId:int}/ticket-history")]
    [ProducesResponseType<CustomerHistoryDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCrmCustomerHistory(int crmCustomerId, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await customerHistoryAppService.GetByCrmCustomerIdAsync(
            employeeId.Value, GetRoles(), crmCustomerId, excludeTicketId: null, limit, cancellationToken);
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

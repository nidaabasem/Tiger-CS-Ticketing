using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// Business-rule change: searches CRM, PACT, and Tasleeh by the intake's own
/// phone number and returns whatever each source found — enrichment/
/// identification for the agent, never a Ticket creation gate. Scoped to
/// CS Agent/CS Supervisor only (PolicyNames.CustomerVerification), same
/// rationale as IntakeRecordsController/CrmController: this is a step of the
/// same intake-then-create sequence those endpoints belong to.
/// </summary>
[ApiController]
[Route("api/intake-records/{intakeRecordId:long}/customer-lookup")]
[Authorize(Policy = PolicyNames.CustomerVerification)]
[Tags(OpenApiTags.CustomerLookup)]
public class CustomerLookupController(CustomerLookupAppService customerLookupAppService) : ControllerBase
{
    /// <summary>Search CRM, PACT, and Tasleeh for the intake's phone number.</summary>
    /// <remarks>
    /// Always 200 with all three sources' outcomes together — a source that
    /// found nothing (NotFound) or could not be reached (Failed) never hides
    /// another source's match, and this call never blocks or gates ticket
    /// creation (<c>POST /api/tickets</c>). Pass a Found CRM source's
    /// unitReferenceId/contactReferenceId straight to ticket creation to
    /// link it; PACT/Tasleeh matches are display-only.
    /// </remarks>
    /// <param name="intakeRecordId">The intake record whose phone number to search with.</param>
    /// <response code="200">CRM, PACT, and Tasleeh's results — one entry per source, each Found/NotFound/Failed.</response>
    /// <response code="404">No such intake record.</response>
    [HttpGet]
    [ProducesResponseType<CustomerLookupResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Search(long intakeRecordId, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await customerLookupAppService.SearchAsync(intakeRecordId, cancellationToken);
        return result.Outcome switch
        {
            CustomerLookupOutcome.Success => Ok(result.Response),
            _ => NotFound()
        };
    }

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }
}

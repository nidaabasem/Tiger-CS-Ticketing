using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// A thin passthrough onto Tiger CRM's read-only data-access port
/// (<c>ICrmGateway</c>) — unit lookup by unit number, and the contacts/
/// owners/tenants/authorized representatives linked to a unit
/// (MVP-API-Contracts.md §2.1-§2.3). This controller makes no verification
/// decision of its own; it exists so an agent (or the verification-session
/// flow below) has something to look up and select from. See
/// <c>CrmUnitLookupAppService</c>'s and <c>VerificationSessionAppService</c>'s
/// remarks for the full ownership boundary between Tiger CRM (read-only)
/// and Tiger CS Ticketing (owns the verification decision).
///
/// <para>
/// Scoped to CS Agent/CS Supervisor only (PolicyNames.CustomerVerification)
/// — not the contract's literal "Agent and above" wording, and not the
/// global AuthenticatedStaff fallback. See
/// PolicyNames.CustomerVerification's remarks for why: Solution-Analysis.md
/// §4.1's Permission Matrix grants ticket-Create to only these two roles,
/// and customer/requester verification exists solely to gate ticket
/// creation. Reporting User, Department Employee, Department Head, CS
/// Manager, GM, Chairman/CEO, and System Administrator are all denied —
/// confirmed, not guessed.
/// </para>
/// </summary>
[ApiController]
[Route("api/crm")]
[Authorize(Policy = PolicyNames.CustomerVerification)]
public class CrmController(CrmUnitLookupAppService crmUnitLookupAppService) : ControllerBase
{
    /// <summary>MVP-API-Contracts.md §2.1.</summary>
    [HttpGet("units/{crmUnitId}")]
    public async Task<IActionResult> GetUnit(string crmUnitId, CancellationToken cancellationToken)
    {
        var result = await crmUnitLookupAppService.GetUnitAsync(crmUnitId, cancellationToken);

        return result.Outcome switch
        {
            CrmLookupOutcome.Success => Ok(result.Response),
            CrmLookupOutcome.NotFound => Problem(
                type: "https://tigercs.internal/problems/unit-not-found",
                title: "Unit not found",
                statusCode: StatusCodes.Status404NotFound),
            _ => Problem(
                type: "https://tigercs.internal/problems/crm-unavailable",
                title: "CRM is currently unavailable",
                statusCode: StatusCodes.Status502BadGateway)
        };
    }

    /// <summary>MVP-API-Contracts.md §2.2.</summary>
    [HttpGet("units/search")]
    public async Task<IActionResult> SearchUnits(
        [FromQuery] string unitNumber, [FromQuery] string? propertyName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(unitNumber))
        {
            return ValidationProblem();
        }

        var result = await crmUnitLookupAppService.SearchUnitsAsync(unitNumber, propertyName, cancellationToken);

        return result.Outcome switch
        {
            CrmLookupOutcome.Success => Ok(result.Units),
            _ => Problem(
                type: "https://tigercs.internal/problems/crm-unavailable",
                title: "CRM is currently unavailable",
                statusCode: StatusCodes.Status502BadGateway)
        };
    }

    /// <summary>MVP-API-Contracts.md §2.3.</summary>
    [HttpGet("units/{crmUnitId}/contacts")]
    public async Task<IActionResult> GetContacts(string crmUnitId, CancellationToken cancellationToken)
    {
        var result = await crmUnitLookupAppService.GetContactsAsync(crmUnitId, cancellationToken);

        return result.Outcome switch
        {
            CrmLookupOutcome.Success => Ok(result.Contacts),
            CrmLookupOutcome.NotFound => Problem(
                type: "https://tigercs.internal/problems/unit-not-found",
                title: "Unit not found",
                statusCode: StatusCodes.Status404NotFound),
            _ => Problem(
                type: "https://tigercs.internal/problems/crm-unavailable",
                title: "CRM is currently unavailable",
                statusCode: StatusCodes.Status502BadGateway)
        };
    }
}

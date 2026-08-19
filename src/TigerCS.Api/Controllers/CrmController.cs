using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.CrmVerification.Dto;
using TigerCS.Application.Modules.CrmVerification.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>
/// MVP-API-Contracts.md §2.1-§2.3, scoped to CS Agent/CS Supervisor only
/// (PolicyNames.CrmVerification) — not the contract's literal "Agent and
/// above" wording, and not the global AuthenticatedStaff fallback. See
/// PolicyNames.CrmVerification's remarks for why: Solution-Analysis.md
/// §4.1's Permission Matrix grants ticket-Create to only these two roles,
/// and CRM verification exists solely to gate ticket creation. Reporting
/// User, Department Employee, Department Head, CS Manager, GM, Chairman/CEO,
/// and System Administrator are all denied — confirmed, not guessed.
/// </summary>
[ApiController]
[Route("api/crm")]
[Authorize(Policy = PolicyNames.CrmVerification)]
public class CrmController(CrmVerificationAppService crmVerificationAppService) : ControllerBase
{
    /// <summary>MVP-API-Contracts.md §2.1.</summary>
    [HttpGet("units/{crmUnitId}")]
    public async Task<IActionResult> GetUnit(string crmUnitId, CancellationToken cancellationToken)
    {
        var result = await crmVerificationAppService.GetUnitAsync(crmUnitId, cancellationToken);

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

        var result = await crmVerificationAppService.SearchUnitsAsync(unitNumber, propertyName, cancellationToken);

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
        var result = await crmVerificationAppService.GetContactsAsync(crmUnitId, cancellationToken);

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

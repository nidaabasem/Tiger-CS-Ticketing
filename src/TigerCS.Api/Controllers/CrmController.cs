using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.CrmVerification.Dto;
using TigerCS.Application.Modules.CrmVerification.Services;

namespace TigerCS.Api.Controllers;

/// <summary>
/// MVP-API-Contracts.md §2.1-§2.3 — "Agent and above." No dedicated
/// AgentOrAbove policy exists in this pilot's flat policy catalog
/// (PolicyNames.cs), so this relies on the global fallback policy (any
/// authenticated, active staff member — Program.cs), same as
/// DepartmentsController's own "any authenticated staff" endpoint.
/// </summary>
[ApiController]
[Route("api/crm")]
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

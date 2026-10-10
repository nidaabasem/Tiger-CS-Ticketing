using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;

namespace TigerCS.Api.Controllers;

/// <summary>Collections by UNIT: the unit-keyed Payment Summary and the status of the bulk CRM owner data both Collections pages link with.</summary>
[ApiController]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[Route("api/collections")]
[Tags(OpenApiTags.Collections)]
public sealed class CollectionsUnitsController(CollectionsUnitPaymentSummaryAppService summary, CollectionsLeasingSummaryAppService leasing, ICollectionsCrmOwnerStore crmOwners, CollectionsAuthorizationService authorization) : ControllerBase
{
    /// <summary>
    /// Payment Summary of one unit, found by its tower + unit code (<c>TP140-101</c>), never by a phone number. The customer comes from CRM first (an eligible, not cancelled
    /// Sold / Contract sale of the same unit), PACT completes what CRM lacks; amounts and instalments come from PACT for that unit only (due today or earlier, Due + Overdue).
    /// A missing PACT mobile does not matter. "PACT holds nothing" is reported as <c>NoFinancialData</c>, never as zero.
    /// </summary>
    /// <param name="unitCode">The unit code, with or without the <c>TP</c> prefix; a cancelled code (containing <c>*</c>), <c>0</c> or empty is refused.</param>
    /// <param name="companyId">Optional 4 or 32 to pin the company when the same code exists in both.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpGet("units/payment-summary")]
    [ProducesResponseType<CollectionsUnitPaymentSummaryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> UnitPaymentSummary([FromQuery] string? unitCode, [FromQuery] int? companyId = null, CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null) return Unauthorized();
        var result = await summary.GetAsync(caller, unitCode, companyId, cancellationToken);
        if (result.IsSuccess) return Ok(result.Value);
        var (http, code) = result.Outcome switch
        {
            CollectionsOutcome.Forbidden => (403, "Forbidden"),
            CollectionsOutcome.InvalidRequest => (400, "InvalidRequest"),
            CollectionsOutcome.Disabled => (503, "CollectionsDisabled"),
            _ => (503, "FinanceUnavailable")
        };
        return Problem(statusCode: http, title: code, detail: result.Detail ?? code, type: $"https://tigercs.internal/problems/collections/{code}");
    }

    /// <summary>
    /// Leasing fallback of the Payment Summary: for a customer with no unit tied to an eligible CRM record, PACT is searched by the NORMALISED mobile under company 7
    /// (<c>+971…</c> and <c>971…</c> are one number). Every contract/unit is a separate entry (company, tenant, unit, contract); ended contracts and other companies are excluded.
    /// Amounts stay <c>NoFinancialData</c> until a company-7 receivables source exists - never zero.
    /// </summary>
    /// <param name="mobile">The customer's mobile in any common format.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpGet("leasing/payment-summary")]
    [ProducesResponseType<CollectionsLeasingSummaryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> LeasingPaymentSummary([FromQuery] string? mobile, CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null) return Unauthorized();
        var result = await leasing.GetAsync(caller, mobile, cancellationToken);
        if (result.IsSuccess) return Ok(result.Value);
        var (http, code) = result.Outcome switch
        {
            CollectionsOutcome.Forbidden => (403, "Forbidden"),
            CollectionsOutcome.InvalidRequest => (400, "InvalidRequest"),
            CollectionsOutcome.Disabled => (503, "CollectionsDisabled"),
            _ => (503, "FinanceUnavailable")
        };
        return Problem(statusCode: http, title: code, detail: result.Detail ?? code, type: $"https://tigercs.internal/problems/collections/{code}");
    }

    /// <summary>State of the bulk CRM owner data (last load, rows, rows that could not be tied to one company, last error). Same permission as reading Collections financials.</summary>
    [HttpGet("crm-owners/status")]
    [ProducesResponseType<CrmOwnerState>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CrmOwnersStatus(CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null) return Unauthorized();
        if (!(await authorization.ResolveAsync(caller, cancellationToken)).CanReadFinancials)
            return Problem(statusCode: 403, title: "Forbidden", type: "https://tigercs.internal/problems/collections/Forbidden");
        return Ok(await crmOwners.GetStateAsync(cancellationToken));
    }

    private CollectionsCaller? Caller() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var employeeId)
        ? new CollectionsCaller(employeeId, User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray(),
            User.FindAll(TigerCsClaimTypes.DepartmentId).Select(c => int.TryParse(c.Value, out var d) ? d : (int?)null).OfType<int>().ToArray())
        : null;
}

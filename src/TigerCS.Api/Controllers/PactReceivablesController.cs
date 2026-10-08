using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;

namespace TigerCS.Api.Controllers;

[ApiController]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[Route("api/collections/receivables/customers")]
[Tags(OpenApiTags.Collections)]
public sealed class PactReceivablesController(PactReceivableCustomersAppService service) : ControllerBase
{
    /// <summary>Lists due and overdue PACT customers for companies 4 and 32, including customers without tickets.</summary>
    /// <param name="companyId">Optional company filter; only 4 or 32 are accepted.</param>
    /// <param name="status">Optional view: <c>all</c> (default), <c>due</c> or <c>overdue</c>.</param>
    /// <param name="search">Optional free-text filter (customer name, unit or phone), up to 200 characters.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="pageSize">Page size, 1 to 100.</param>
    /// <param name="year">Reporting year; supply together with <c>month</c>. Defaults to the current Dubai month when both are omitted.</param>
    /// <param name="month">Reporting month, 1-12.</param>
    /// <param name="towerId">Optional tower (local <c>CollectionsTowers.TowerId</c>); the company is resolved from the tower. Omit for all towers.</param>
    /// <param name="dateFrom">First instalment due date included. Defaults to 1 January of the report year.</param>
    /// <param name="dateTo">Last instalment due date included (whole day). Defaults to the end of the reporting month.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpGet]
    [ProducesResponseType<PactReceivableCustomersDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> List([FromQuery] int? companyId, [FromQuery] string? status,
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        [FromQuery] int? year = null, [FromQuery] int? month = null, CancellationToken cancellationToken = default,
        [FromQuery] int? towerId = null, [FromQuery] DateOnly? dateFrom = null, [FromQuery] DateOnly? dateTo = null)
    {
        var caller = Caller();
        if (caller is null) return Unauthorized();
        var result = await service.ListAsync(caller, companyId, status, search, page, pageSize, year, month, cancellationToken, towerId, dateFrom, dateTo);
        if (result.IsSuccess) return Ok(result.Value);
        return Failure(result.Outcome, result.Detail);
    }

    /// <summary>Active towers (number and name) for the tower dropdown. Read from the local table; PACT is not contacted.</summary>
    [HttpGet("/api/collections/receivables/towers")]
    [ProducesResponseType<IReadOnlyList<CollectionsTowerDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Towers(CancellationToken cancellationToken = default)
    {
        var caller = Caller();
        if (caller is null) return Unauthorized();
        var result = await service.ListTowersAsync(caller, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : Failure(result.Outcome, result.Detail);
    }

    private CollectionsCaller? Caller() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var employeeId)
        ? new CollectionsCaller(employeeId, User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray(),
            User.FindAll(TigerCsClaimTypes.DepartmentId).Select(c => int.TryParse(c.Value, out var d) ? d : (int?)null).OfType<int>().ToArray())
        : null;

    private IActionResult Failure(CollectionsOutcome outcome, string? detail)
    {
        var (http, code) = outcome switch
        {
            CollectionsOutcome.Forbidden => (403, "Forbidden"),
            CollectionsOutcome.InvalidRequest => (400, "InvalidRequest"),
            CollectionsOutcome.Disabled => (503, "CollectionsDisabled"),
            _ => (503, "FinanceUnavailable")
        };
        var response = Problem(statusCode: http, title: code, detail: detail ?? code,
            type: $"https://tigercs.internal/problems/collections/{code}");
        if (response.Value is ProblemDetails problem)
        {
            problem.Extensions["code"] = code;
            problem.Extensions["message"] = detail ?? code;
        }
        return response;
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

/// <summary>The category directory — used to populate the category picker on ticket creation (FR-CLS-01/FR-RTE-01).</summary>
[ApiController]
[Route("api/categories")]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[Tags(OpenApiTags.Categories)]
public class CategoriesController(CategoryQueryAppService categoryQueryAppService) : ControllerBase
{
    /// <summary>List every active category. Any authenticated staff member may view.</summary>
    /// <response code="200">The active categories.</response>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<CategoryResponseDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var result = await categoryQueryAppService.ListActiveAsync(cancellationToken);
        return Ok(result);
    }
}

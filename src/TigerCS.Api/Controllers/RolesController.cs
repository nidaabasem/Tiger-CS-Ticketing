using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

namespace TigerCS.Api.Controllers;

[ApiController]
[Route("api/roles")]
[Authorize(Policy = PolicyNames.SystemAdministrator)]
public class RolesController(RoleCatalogAppService roleCatalogAppService) : ControllerBase
{
    /// <summary>MVP-API-Contracts.md §1.5.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var roles = await roleCatalogAppService.ListRolesAsync(cancellationToken);
        return Ok(roles);
    }
}

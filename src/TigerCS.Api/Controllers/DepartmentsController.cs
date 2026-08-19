using Microsoft.AspNetCore.Mvc;
using TigerCS.Application.Modules.IdentityAndAccess.Services;

namespace TigerCS.Api.Controllers;

[ApiController]
[Route("api/departments")]
public class DepartmentsController(DepartmentUserAppService departmentUserAppService) : ControllerBase
{
    /// <summary>MVP-API-Contracts.md §1.4 — any authenticated staff may view.</summary>
    [HttpGet("{departmentId:int}/users")]
    public async Task<IActionResult> GetUsers(
        int departmentId,
        [FromQuery] bool activeOnly = true,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(page, 1);

        var result = await departmentUserAppService.ListAsync(departmentId, activeOnly, page, pageSize, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }
}

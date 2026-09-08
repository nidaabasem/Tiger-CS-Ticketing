using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.Administration.Services;

namespace TigerCS.Api.Controllers;

/// <summary>User administration — System Administrator only. Users are deactivated, never deleted.</summary>
[Route("api/admin/users")]
[Authorize(Policy = PolicyNames.SystemAdministrator)]
[Tags(OpenApiTags.Administration)]
public class AdminUsersController(AdminUserAppService users) : AdminControllerBase
{
    /// <summary>Lists users with optional search (display name, user name, e-mail) and inactive users included on request.</summary>
    [HttpGet]
    [ProducesResponseType<AdminUserListDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] string? search, [FromQuery] bool includeInactive = false,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default) =>
        Ok(await users.ListAsync(search, includeInactive, page, pageSize, cancellationToken));

    /// <summary>One user with roles, department memberships and whether ticket history references them.</summary>
    [HttpGet("{employeeId:guid}")]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid employeeId, CancellationToken cancellationToken)
    {
        var user = await users.GetAsync(employeeId, cancellationToken);
        return user is null ? NotFound() : Ok(user);
    }

    /// <summary>Creates the Identity account (Identity validates the initial password) and the employee profile.</summary>
    [HttpPost]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateUserRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await users.CreateAsync(CallerEmployeeId, request, cancellationToken),
            created => CreatedAtAction(nameof(Get), new { employeeId = created.EmployeeId }, created));

    /// <summary>Edits the supported profile fields (display name, e-mail, Geyness-staff flag).</summary>
    [HttpPut("{employeeId:guid}/profile")]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateProfile(Guid employeeId, [FromBody] UpdateUserProfileRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await users.UpdateProfileAsync(CallerEmployeeId, employeeId, request, cancellationToken));

    /// <summary>Activate/deactivate — the existing last-active-administrator guard applies.</summary>
    [HttpPatch("{employeeId:guid}/activation")]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetActivation(Guid employeeId, [FromBody] SetActiveRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await users.SetActivationAsync(CallerEmployeeId, employeeId, request, cancellationToken));

    /// <summary>Replaces the user's roles with exactly the given fixed roles.</summary>
    [HttpPut("{employeeId:guid}/roles")]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetRoles(Guid employeeId, [FromBody] SetUserRolesRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await users.SetRolesAsync(CallerEmployeeId, employeeId, request, cancellationToken));

    /// <summary>Adds a department membership using the existing membership model.</summary>
    [HttpPost("{employeeId:guid}/departments")]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddDepartment(Guid employeeId, [FromBody] AddDepartmentMembershipRequestDto request, CancellationToken cancellationToken) =>
        FromResult(await users.AddDepartmentAsync(CallerEmployeeId, employeeId, request, cancellationToken));

    /// <summary>Ends a department membership; the primary membership cannot be removed while others remain.</summary>
    [HttpDelete("{employeeId:guid}/departments/{departmentId:int}")]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveDepartment(Guid employeeId, int departmentId, CancellationToken cancellationToken) =>
        FromResult(await users.RemoveDepartmentAsync(CallerEmployeeId, employeeId, departmentId, cancellationToken));
}

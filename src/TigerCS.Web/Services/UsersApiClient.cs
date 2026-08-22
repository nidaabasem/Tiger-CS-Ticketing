using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Web.Infrastructure;

namespace TigerCS.Web.Services;

/// <summary>
/// <c>GET /api/users/me</c> (MVP-API-Contracts.md §1.3) and
/// <c>GET /api/departments/{id}/users</c> (§1.4).
/// </summary>
public sealed class UsersApiClient(TigerCsApiClient api)
{
    /// <summary>
    /// The caller's own profile, including the department memberships the
    /// shell needs. Resolved by the API from the token's subject claim, never
    /// from anything this application sends.
    /// </summary>
    public Task<ApiResult<CurrentUserResponseDto>> GetCurrentUserAsync(CancellationToken cancellationToken) =>
        api.GetAsync<CurrentUserResponseDto>("/api/users/me", cancellationToken);

    /// <summary>
    /// The members of one department.
    ///
    /// <para>
    /// This is the only endpoint in the whole API that maps an employee id to
    /// a display name, which makes it the sole source for both the assignment
    /// picker and the queue's owner column. It is also correct for the picker
    /// on its own terms: <c>POST /api/tickets/{id}/assignment</c> requires the
    /// target to be an active member of the ticket's current department, so
    /// the department roster is exactly the eligible set.
    /// </para>
    /// </summary>
    public Task<ApiResult<PagedResultDto<DepartmentUserDto>>> GetDepartmentUsersAsync(
        int departmentId, int page, int pageSize, CancellationToken cancellationToken) =>
        api.GetAsync<PagedResultDto<DepartmentUserDto>>(
            $"/api/departments/{departmentId}/users?activeOnly=true&page={page}&pageSize={pageSize}",
            cancellationToken);
}

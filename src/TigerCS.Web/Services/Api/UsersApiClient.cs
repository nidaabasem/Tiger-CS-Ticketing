using System.Web;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>Calls TigerCS.Api's <c>api/users</c> and <c>api/departments</c> endpoints.</summary>
public sealed class UsersApiClient(HttpClient httpClient) : ApiClientBase(httpClient)
{
    public Task<ApiResult<CurrentUserResponseDto>> GetMeAsync(CancellationToken cancellationToken) =>
        GetAsync<CurrentUserResponseDto>("api/users/me", cancellationToken);

    /// <summary>
    /// The only member-listing endpoint the Api exposes. There is no
    /// department-name or employee-name lookup by id — see the contract
    /// gaps documented in the PR description — so this is used as a
    /// best-effort resolver: page through a department's members looking
    /// for one specific employee id.
    /// </summary>
    public Task<ApiResult<PagedResultDto<DepartmentUserDto>>> GetDepartmentUsersAsync(
        int departmentId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["activeOnly"] = "true";
        query["page"] = page.ToString();
        query["pageSize"] = pageSize.ToString();

        return GetAsync<PagedResultDto<DepartmentUserDto>>($"api/departments/{departmentId}/users?{query}", cancellationToken);
    }
}

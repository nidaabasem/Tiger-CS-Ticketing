using TigerCS.Application.Modules.IdentityAndAccess.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>Calls TigerCS.Api's <c>api/departments</c> endpoint.</summary>
public sealed class DepartmentsApiClient(HttpClient httpClient) : ApiClientBase(httpClient)
{
    public Task<ApiResult<IReadOnlyCollection<DepartmentDto>>> GetDepartmentsAsync(CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyCollection<DepartmentDto>>("api/departments", cancellationToken);
}

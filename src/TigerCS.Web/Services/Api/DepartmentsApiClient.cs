using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>Calls TigerCS.Api's <c>api/departments</c> endpoint.</summary>
public sealed class DepartmentsApiClient(HttpClient httpClient, ILogger<DepartmentsApiClient> logger) : ApiClientBase(httpClient, logger)
{
    public Task<ApiResult<IReadOnlyCollection<DepartmentDto>>> GetDepartmentsAsync(CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyCollection<DepartmentDto>>("api/departments", cancellationToken);
}

using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>Calls TigerCS.Api's <c>api/departments</c> endpoint.</summary>
public sealed class DepartmentsApiClient(HttpClient httpClient, ILogger<DepartmentsApiClient> logger) : ApiClientBase(httpClient, logger)
{
    /// <summary>The active Department directory — what a Department picker may offer.</summary>
    public Task<ApiResult<IReadOnlyCollection<DepartmentDto>>> GetDepartmentsAsync(CancellationToken cancellationToken) =>
        GetDepartmentsAsync(activeOnly: true, cancellationToken);

    /// <summary>
    /// The Department directory. <paramref name="activeOnly"/> false includes
    /// deactivated departments — needed only to put a NAME on a department a
    /// ticket already belongs to; a picker never offers those.
    /// </summary>
    public Task<ApiResult<IReadOnlyCollection<DepartmentDto>>> GetDepartmentsAsync(bool activeOnly, CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyCollection<DepartmentDto>>($"api/departments?activeOnly={(activeOnly ? "true" : "false")}", cancellationToken);
}

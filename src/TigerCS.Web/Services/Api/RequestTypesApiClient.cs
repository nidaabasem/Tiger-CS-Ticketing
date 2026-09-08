using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Administration.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>Calls TigerCS.Api's <c>api/request-types</c> directory — the New Ticket wizard's Request Type picker (active request types of one department).</summary>
public sealed class RequestTypesApiClient(HttpClient httpClient, ILogger<RequestTypesApiClient> logger) : ApiClientBase(httpClient, logger)
{
    public Task<ApiResult<IReadOnlyList<RequestTypeOptionDto>>> GetOptionsAsync(int? departmentId, CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyList<RequestTypeOptionDto>>(
            departmentId is { } id ? $"api/request-types?departmentId={id}" : "api/request-types", cancellationToken);
}

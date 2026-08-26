using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>Calls TigerCS.Api's <c>api/intake-records/{id}/customer-lookup</c> endpoint.</summary>
public sealed class CustomerLookupApiClient(HttpClient httpClient, ILogger<CustomerLookupApiClient> logger) : ApiClientBase(httpClient, logger)
{
    public Task<ApiResult<CustomerLookupResultDto>> SearchAsync(long intakeRecordId, CancellationToken cancellationToken) =>
        GetAsync<CustomerLookupResultDto>($"api/intake-records/{intakeRecordId}/customer-lookup", cancellationToken);
}

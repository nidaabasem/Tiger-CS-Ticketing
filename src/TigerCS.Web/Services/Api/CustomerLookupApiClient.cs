using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>Calls TigerCS.Api's <c>api/intake-records/{id}/customer-lookup</c> endpoint.</summary>
public sealed class CustomerLookupApiClient(HttpClient httpClient) : ApiClientBase(httpClient)
{
    public Task<ApiResult<CustomerLookupResultDto>> SearchAsync(long intakeRecordId, CancellationToken cancellationToken) =>
        GetAsync<CustomerLookupResultDto>($"api/intake-records/{intakeRecordId}/customer-lookup", cancellationToken);
}

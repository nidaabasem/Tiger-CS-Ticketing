using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>Calls TigerCS.Api's <c>api/intake-records</c> endpoint.</summary>
public sealed class IntakeRecordsApiClient(HttpClient httpClient) : ApiClientBase(httpClient)
{
    public Task<ApiResult<IntakeRecordResponseDto>> CreateAsync(
        CreateIntakeRecordRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<CreateIntakeRecordRequestDto, IntakeRecordResponseDto>("api/intake-records", request, cancellationToken);
}

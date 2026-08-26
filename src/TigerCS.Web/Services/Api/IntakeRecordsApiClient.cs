using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>Calls TigerCS.Api's <c>api/intake-records</c> endpoint.</summary>
public sealed class IntakeRecordsApiClient(HttpClient httpClient, ILogger<IntakeRecordsApiClient> logger) : ApiClientBase(httpClient, logger)
{
    public Task<ApiResult<IntakeRecordResponseDto>> CreateAsync(
        CreateIntakeRecordRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<CreateIntakeRecordRequestDto, IntakeRecordResponseDto>("api/intake-records", request, cancellationToken);
}

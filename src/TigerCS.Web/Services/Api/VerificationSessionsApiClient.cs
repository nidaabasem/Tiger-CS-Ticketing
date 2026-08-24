using TigerCS.Application.Modules.CustomerVerification.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>Calls TigerCS.Api's <c>api/verification-sessions</c> endpoints.</summary>
public sealed class VerificationSessionsApiClient(HttpClient httpClient) : ApiClientBase(httpClient)
{
    public Task<ApiResult<VerificationSessionResponseDto>> CreateAsync(
        CreateVerificationSessionRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<CreateVerificationSessionRequestDto, VerificationSessionResponseDto>("api/verification-sessions", request, cancellationToken);
}

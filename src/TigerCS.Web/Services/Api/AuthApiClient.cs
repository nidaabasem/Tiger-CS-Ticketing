using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>Calls TigerCS.Api's <c>api/auth</c> endpoints.</summary>
public sealed class AuthApiClient(HttpClient httpClient, ILogger<AuthApiClient> logger) : ApiClientBase(httpClient, logger)
{
    public Task<ApiResult<LoginResponseDto>> LoginAsync(LoginRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<LoginRequestDto, LoginResponseDto>("api/auth/login", request, cancellationToken);

    /// <summary>Redeems a Genesys Secure Screen Pop token for the mapped agent's ordinary session (anonymous — the token is the credential).</summary>
    public Task<ApiResult<ScreenPopSessionResponseDto>> RedeemScreenPopAsync(string token, CancellationToken cancellationToken) =>
        PostAsync<ScreenPopRedeemRequestDto, ScreenPopSessionResponseDto>(
            "api/auth/screen-pop/redeem", new ScreenPopRedeemRequestDto(token), cancellationToken);

    public Task<ApiResult> LogoutAsync(CancellationToken cancellationToken) =>
        PostAsync("api/auth/logout", new { }, cancellationToken);

    /// <summary>
    /// Changes the signed-in user's own password — 204 on success, after which
    /// the session's token is invalid and the user must sign in again; 422 with
    /// the reason in <c>Detail</c> for a wrong current password or a policy
    /// failure. Needs the bearer token, so this client carries
    /// <c>BearerTokenHandler</c> like every authenticated client.
    /// </summary>
    public Task<ApiResult> ChangePasswordAsync(ChangePasswordRequestDto request, CancellationToken cancellationToken) =>
        PostAsync("api/auth/change-password", request, cancellationToken);
}

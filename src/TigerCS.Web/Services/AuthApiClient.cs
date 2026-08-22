using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Web.Infrastructure;

namespace TigerCS.Web.Services;

/// <summary>
/// <c>POST /api/auth/login</c> and <c>POST /api/auth/logout</c>
/// (MVP-API-Contracts.md §1.1/§1.2).
/// </summary>
public sealed class AuthApiClient(TigerCsApiClient api)
{
    /// <summary>
    /// Signs in. The API answers 200 with a token, 400 for a blank field, 401
    /// for a rejected combination, and 423 for a locked account - the caller
    /// distinguishes them from <see cref="ApiProblem.StatusCode"/>.
    /// </summary>
    public Task<ApiResult<LoginResponseDto>> LoginAsync(
        string username, string password, CancellationToken cancellationToken) =>
        api.PostAsync<LoginResponseDto>(
            "/api/auth/login", new LoginRequestDto(username, password), cancellationToken);

    /// <summary>
    /// Tells the API the session is over. Answers 204 regardless, so a failure
    /// here never blocks the local sign-out - the cookie is cleared either way.
    /// </summary>
    public Task<ApiResult> LogoutAsync(CancellationToken cancellationToken) =>
        api.PostAsync("/api/auth/logout", null, cancellationToken);
}

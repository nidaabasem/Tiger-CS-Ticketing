namespace TigerCS.Application.Modules.IdentityAndAccess.Abstractions;

public sealed record IssuedToken(string AccessToken, DateTime ExpiresAtUtc);

/// <summary>
/// Issues the JWT bearer access token described in MVP-API-Contracts.md §1.1.
/// No refresh token — none is specified in the approved API contract.
/// </summary>
public interface ITokenService
{
    IssuedToken CreateAccessToken(Guid employeeId, string displayName, IReadOnlyCollection<string> roles);
}

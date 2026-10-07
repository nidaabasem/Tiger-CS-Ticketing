namespace TigerCS.Application.Modules.IdentityAndAccess.Abstractions;

public sealed record IssuedToken(string AccessToken, DateTime ExpiresAtUtc);

/// <summary>The claim names TigerCS adds to its JWT beyond the standard subject/name/role claims.</summary>
public static class TigerCsTokenClaims
{
    /// <summary>
    /// The user's ASP.NET Core Identity security stamp at the moment the token
    /// was issued. Compared against the account's current stamp on every
    /// request (<c>ActiveEmployeeHandler</c>), so rotating the stamp — which
    /// every password reset or change does — invalidates every token issued
    /// before it without a revocation list.
    /// </summary>
    public const string SecurityStamp = "sst";
}

/// <summary>
/// Issues the JWT bearer access token described in MVP-API-Contracts.md §1.1.
/// No refresh token — none is specified in the approved API contract.
/// </summary>
public interface ITokenService
{
    /// <summary>
    /// Issues the token for the employee. <paramref name="securityStamp"/> is
    /// the account's Identity security stamp at issue time, carried as the
    /// <see cref="TigerCsTokenClaims.SecurityStamp"/> claim.
    /// </summary>
    IssuedToken CreateAccessToken(Guid employeeId, string displayName, IReadOnlyCollection<string> roles, string securityStamp);
}

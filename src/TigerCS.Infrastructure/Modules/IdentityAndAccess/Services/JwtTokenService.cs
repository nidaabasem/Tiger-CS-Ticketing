using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Infrastructure.Identity;

namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;

/// <summary>
/// MVP-API-Contracts.md §1.1: issues the JWT bearer access token. UTC
/// expiration; no refresh token (none is specified in the approved
/// contract, so none is invented here). The token also carries the
/// account's Identity security stamp (<see cref="TigerCsTokenClaims.SecurityStamp"/>),
/// which is the whole invalidation mechanism: a password reset or change
/// rotates the stamp, and every earlier token then fails the per-request
/// check in <c>ActiveEmployeeHandler</c>.
/// </summary>
public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider timeProvider) : ITokenService
{
    private readonly JwtOptions _options = options.Value;

    public IssuedToken CreateAccessToken(Guid employeeId, string displayName, IReadOnlyCollection<string> roles, string securityStamp)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expiresAtUtc = now.AddMinutes(_options.ExpirationMinutes);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, employeeId.ToString()),
            new(ClaimTypes.Name, displayName),
            new(TigerCsTokenClaims.SecurityStamp, securityStamp)
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = now,
            Expires = expiresAtUtc,
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256)
        };

        var handler = new JsonWebTokenHandler();
        var token = handler.CreateToken(descriptor);

        return new IssuedToken(token, expiresAtUtc);
    }
}

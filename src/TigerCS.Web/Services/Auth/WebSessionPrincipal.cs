using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace TigerCS.Web.Services.Auth;

/// <summary>
/// The one definition of what a TigerCS Web session carries — used by the
/// password login and by Genesys Screen Pop alike, so a Screen Pop session is
/// indistinguishable from a normal one: the same claims, the same Api access
/// token, and therefore the same authorization everywhere.
/// </summary>
public static class WebSessionPrincipal
{
    public static ClaimsPrincipal Create(
        Guid employeeId, string displayName, string accessToken, IEnumerable<string> roles, int? primaryDepartmentId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, employeeId.ToString()),
            new(ClaimTypes.Name, displayName),
            new(TigerCsClaimTypes.AccessToken, accessToken),
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        if (primaryDepartmentId is int departmentId)
        {
            claims.Add(new Claim(TigerCsClaimTypes.PrimaryDepartmentId, departmentId.ToString()));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}

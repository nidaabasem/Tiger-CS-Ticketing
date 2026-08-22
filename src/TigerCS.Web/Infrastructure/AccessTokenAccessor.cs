using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace TigerCS.Web.Infrastructure;

/// <summary>
/// Reads the signed-in user's API access token out of their authentication
/// cookie.
///
/// <para>
/// One of exactly two types permitted to touch the token; the other is
/// <see cref="ApiAuthenticationHandler"/>, which consumes what this returns.
/// Keeping the read in one place is what makes "the token is never logged and
/// never leaves the server" checkable rather than merely intended.
/// </para>
/// </summary>
public sealed class AccessTokenAccessor(IHttpContextAccessor httpContextAccessor)
{
    /// <summary>The current user's access token, or null when there is no authenticated user.</summary>
    public string? GetAccessToken() =>
        httpContextAccessor.HttpContext?.User.FindFirst(TigerCsClaimTypes.AccessToken)?.Value;

    /// <summary>When the current token expires, or null when unknown.</summary>
    public DateTimeOffset? GetExpiresAtUtc()
    {
        var raw = httpContextAccessor.HttpContext?.User.FindFirst(TigerCsClaimTypes.AccessTokenExpiresAtUtc)?.Value;

        return DateTimeOffset.TryParseExact(
            raw, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// True when the cookie still carries a token that has not expired.
    /// The API is still the authority - this only avoids sending a call that
    /// is already known to be answerable with 401.
    /// </summary>
    public bool HasUsableToken()
    {
        if (string.IsNullOrEmpty(GetAccessToken()))
        {
            return false;
        }

        var expiresAt = GetExpiresAtUtc();
        return expiresAt is null || expiresAt > DateTimeOffset.UtcNow;
    }

    /// <summary>The current user's display name, or an empty string when signed out.</summary>
    public string GetDisplayName() =>
        httpContextAccessor.HttpContext?.User.FindFirst(ClaimTypes.Name)?.Value ?? string.Empty;

    /// <summary>The current user's employee id, or null when signed out.</summary>
    public Guid? GetEmployeeId()
    {
        var raw = httpContextAccessor.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var employeeId) ? employeeId : null;
    }

    /// <summary>Every role name on the current principal.</summary>
    public IReadOnlyCollection<string> GetRoles() =>
        httpContextAccessor.HttpContext?.User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray() ?? [];

    /// <summary>The departments the current user is assigned to, as id/name pairs.</summary>
    public IReadOnlyDictionary<int, string> GetDepartments()
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user is null)
        {
            return new Dictionary<int, string>();
        }

        var departments = new Dictionary<int, string>();
        foreach (var claim in user.FindAll(TigerCsClaimTypes.DepartmentName))
        {
            var separator = claim.Value.IndexOf(':');
            if (separator > 0
                && int.TryParse(claim.Value[..separator], CultureInfo.InvariantCulture, out var departmentId))
            {
                departments[departmentId] = claim.Value[(separator + 1)..];
            }
        }

        return departments;
    }
}

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace TigerCS.Web.Models;

/// <summary>
/// Where the agent was in the Customers directory (search, filters, page)
/// before opening a Customer Profile — remembered in a same-site session
/// cookie written by the Customers page, so the profile's breadcrumb and
/// back link return to that exact list. The Customers counterpart of
/// <see cref="TicketsContext"/>.
/// </summary>
public static class CustomersContext
{
    public const string CookieName = "TigerCS.Web.CustomersContext";
    public const string BasePath = "/Customers";

    private static readonly HashSet<string> AllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "search", "verificationSource", "departmentId", "openOnly", "page", "pageSize",
    };

    public static string ToCookieValue(IEnumerable<KeyValuePair<string, StringValues>> query) =>
        QueryString.Create(query.Where(pair => AllowedKeys.Contains(pair.Key))).Value?.TrimStart('?') ?? string.Empty;

    /// <summary>The remembered list URL, or the bare directory when nothing valid is remembered.</summary>
    public static string HrefFromCookieValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return BasePath;
        }

        Dictionary<string, StringValues> parsed;
        try
        {
            parsed = QueryHelpers.ParseQuery(value);
        }
        catch (Exception)
        {
            return BasePath;
        }

        var kept = parsed
            .Where(pair => AllowedKeys.Contains(pair.Key) && pair.Value.Count > 0 && !string.IsNullOrEmpty(pair.Value[0]))
            .Select(pair => new KeyValuePair<string, StringValues>(pair.Key, pair.Value[0]))
            .ToList();
        var query = QueryString.Create(kept);
        return query.HasValue ? $"{BasePath}{query.Value}" : BasePath;
    }
}

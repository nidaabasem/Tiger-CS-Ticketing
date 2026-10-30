using Microsoft.Extensions.Primitives;

namespace TigerCS.Web.Services.Embedding;

/// <summary>
/// <c>Embedding</c> configuration section: the exact origins allowed to show
/// Tiger Ticketing inside a frame (Genesys Cloud embedded app). Empty (the
/// default) means nobody may frame the site.
/// </summary>
public sealed class EmbeddingOptions
{
    public const string SectionName = "Embedding";

    public string[] AllowedFrameAncestors { get; set; } = [];
}

/// <summary>
/// Turns the configured frame-ancestor origins into the browser-facing
/// controls (CSP <c>frame-ancestors</c>, <c>X-Frame-Options</c>, cookie
/// SameSite). Each origin must be an exact <c>https://host[:port]</c>:
/// wildcards, <c>*</c>, <c>http</c>, paths and credentials are rejected at
/// startup so the allow-list cannot silently widen into "anyone may frame us".
/// </summary>
public sealed class EmbeddingPolicy
{
    public const string AuthCookieName = "TigerCS.Web.Auth";
    public const string AntiforgeryCookieName = "TigerCS.Web.Antiforgery";

    private EmbeddingPolicy(IReadOnlyList<string> origins) => AllowedOrigins = origins;

    public IReadOnlyList<string> AllowedOrigins { get; }

    public bool IsEmbeddingEnabled => AllowedOrigins.Count > 0;

    /// <summary>The CSP directive value: <c>'none'</c> unless origins are configured.</summary>
    public string FrameAncestors => IsEmbeddingEnabled ? string.Join(' ', AllowedOrigins) : "'none'";

    /// <summary>
    /// <c>DENY</c> when embedding is off. When on, no header at all: the only
    /// standard value that can name an origin (<c>ALLOW-FROM</c>) is obsolete
    /// and ignored by browsers, and <c>SAMEORIGIN</c>/<c>DENY</c> would block
    /// Genesys. CSP <c>frame-ancestors</c> is then the sole, precise control.
    /// </summary>
    public string? XFrameOptions => IsEmbeddingEnabled ? null : "DENY";

    /// <summary>
    /// Cookies must be sent in a cross-site frame, so embedding needs
    /// <c>SameSite=None</c> (which browsers only accept with <c>Secure</c>).
    /// Otherwise the stricter <c>Strict</c> stays.
    /// </summary>
    public SameSiteMode CookieSameSite => IsEmbeddingEnabled ? SameSiteMode.None : SameSiteMode.Strict;

    public CookieSecurePolicy CookieSecurePolicy => IsEmbeddingEnabled ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;

    public static EmbeddingPolicy Create(IEnumerable<string>? configuredOrigins)
    {
        var origins = new List<string>();
        foreach (var raw in configuredOrigins ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var origin = Normalize(raw.Trim());
            if (!origins.Contains(origin, StringComparer.Ordinal)) origins.Add(origin);
        }

        return new EmbeddingPolicy(origins);
    }

    private static string Normalize(string value)
    {
        if (value.Contains('*') || value.Contains(' ') || value.Contains(';') || value.Contains(','))
        {
            throw new InvalidOperationException(
                $"Embedding:AllowedFrameAncestors entry '{value}' is not allowed: wildcards and multiple values in one entry are rejected.");
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.AbsolutePath != "/" && uri.AbsolutePath != string.Empty))
        {
            throw new InvalidOperationException(
                $"Embedding:AllowedFrameAncestors entry '{value}' must be an exact https origin such as https://apps.mypurecloud.de (no path, query or credentials).");
        }

        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }

    /// <summary>
    /// Adds the <c>Partitioned</c> (CHIPS) attribute to the auth and
    /// antiforgery cookies so browsers that restrict third-party cookies still
    /// keep them, scoped to the embedding top-level site. Other cookies and
    /// already-partitioned values are returned unchanged.
    /// </summary>
    public static StringValues MarkSessionCookiesPartitioned(StringValues setCookieHeaders)
    {
        if (setCookieHeaders.Count == 0) return setCookieHeaders;

        var result = new string[setCookieHeaders.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var cookie = setCookieHeaders[i] ?? string.Empty;
            var isSessionCookie = cookie.StartsWith(AuthCookieName + "=", StringComparison.Ordinal)
                || cookie.StartsWith(AntiforgeryCookieName + "=", StringComparison.Ordinal);
            result[i] = isSessionCookie && !cookie.Contains("; partitioned", StringComparison.OrdinalIgnoreCase)
                ? cookie + "; partitioned"
                : cookie;
        }

        return result;
    }
}

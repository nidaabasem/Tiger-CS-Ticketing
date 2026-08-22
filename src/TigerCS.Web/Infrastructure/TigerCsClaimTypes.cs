namespace TigerCS.Web.Infrastructure;

/// <summary>
/// Claim types this application stores in its own authentication cookie.
///
/// <para>
/// These are the web tier's claims, not the API's. The API's JWT is the
/// authority on identity and roles; this cookie is a server-encrypted
/// container for that JWT plus the identity values the shell needs to render
/// without an API round trip on every request.
/// </para>
/// </summary>
public static class TigerCsClaimTypes
{
    /// <summary>
    /// The API access token.
    ///
    /// <para>
    /// This claim only ever exists inside the authentication cookie, which is
    /// encrypted and signed by ASP.NET Core Data Protection and marked
    /// HttpOnly. The browser receives ciphertext and no script can read it;
    /// the value is decrypted server-side, per request, solely to set the
    /// Authorization header on an outbound API call. It is never written to a
    /// log, never rendered into a page, and never placed in a URL - see
    /// <see cref="AccessTokenAccessor"/> and
    /// <see cref="ApiAuthenticationHandler"/>, which are the only two types
    /// that read it.
    /// </para>
    /// </summary>
    public const string AccessToken = "tigercs:access_token";

    /// <summary>When the access token stops being accepted (round-trip "o" format, UTC).</summary>
    public const string AccessTokenExpiresAtUtc = "tigercs:access_token_expires_at";

    /// <summary>The caller's primary department id, when they have one.</summary>
    public const string PrimaryDepartmentId = "tigercs:primary_department_id";

    /// <summary>A department the caller is assigned to. Repeated once per membership.</summary>
    public const string DepartmentId = "tigercs:department_id";

    /// <summary>Display name of a department the caller is assigned to, as "{id}:{name}".</summary>
    public const string DepartmentName = "tigercs:department_name";
}

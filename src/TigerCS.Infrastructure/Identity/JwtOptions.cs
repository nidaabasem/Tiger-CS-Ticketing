namespace TigerCS.Infrastructure.Identity;

/// <summary>
/// Bound from configuration ("Jwt" section) — appsettings.Development.json
/// for Issuer/Audience/ExpirationMinutes, user-secrets/environment variables
/// for SigningKey (never committed; see docs/DEV-SETUP.md).
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string SigningKey { get; set; } = string.Empty;
    public int ExpirationMinutes { get; set; } = 60;
}

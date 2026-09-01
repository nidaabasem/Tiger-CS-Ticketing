namespace TigerCS.Integrations.Modules.PactIntegration;

/// <summary>
/// The "PactApi" configuration section — the real PACT HTTP integration's
/// connection settings, used only by <see cref="PactCustomerHttpGateway"/>.
/// Deliberately separate from <see cref="PactGatewayOptions"/> ("Pact",
/// which only selects the provider), the same way <c>Crm:BaseUrl</c>/<c>Crm:SecretKey</c>
/// are the connection half of the CRM section. Neither value is ever
/// hard-coded: a missing/blank value never prevents the host from starting —
/// <see cref="PactCustomerHttpGateway"/> turns it into an <c>Unavailable</c>
/// outcome on first use instead.
/// </summary>
public sealed class PactApiOptions
{
    public const string SectionName = "PactApi";

    /// <summary>
    /// PACT's base URL (e.g. <c>http://pact.tigergroup.internal:5020/</c>).
    /// Not a secret — safe to commit per environment in
    /// <c>appsettings.{Environment}.json</c>, same as <c>Crm:BaseUrl</c>.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// The API key PACT validates via the <c>X-API-KEY</c> request header.
    /// Never committed: configure via user-secrets locally or the
    /// <c>PactApi__ApiKey</c> environment variable in CI/UAT/Production,
    /// exactly like <c>Crm:SecretKey</c> (docs/DEV-SETUP.md §3a/§3b).
    /// </summary>
    public string? ApiKey { get; set; }
}

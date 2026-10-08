namespace TigerCS.Integrations.Modules.CrmIntegration;

/// <summary>
/// The "Crm" configuration section. <see cref="Provider"/> governs what the
/// <see cref="TigerCS.Application.Modules.CustomerVerification.CrmIntegration.ICrmGateway"/>
/// and <c>ICrmCustomerLookupGateway</c> ports resolve
/// (<see cref="IntegrationsServiceCollectionExtensions"/>): "Http" — the
/// standard for Development, UAT and Production — or "Mock" — the automated
/// test host's <see cref="MockCrmGateway"/> fixture data, refused outside
/// Development/Testing by <see cref="CrmGatewaySafety"/>. Tiger CRM publishes
/// no endpoint for those two ports yet, so "Http" resolves
/// <see cref="UnimplementedCrmHttpGateway"/>, which fails closed and never
/// serves fixture data; see its remarks for what the CRM side still owes.
///
/// <para>
/// <see cref="BaseUrl"/> and <see cref="SecretKey"/> are a separate,
/// unconditional real integration: the CRM Buyer Lookup increment's
/// <c>GET /TicketingSystem/GetBuyerByPhone</c> endpoint has already been
/// implemented and manually verified CRM-side, so
/// <c>CrmBuyerHttpGateway</c> always calls it for real — there is no Mock
/// alternative for this one port, and no <c>Provider</c> switch governs it.
/// </para>
/// </summary>
public sealed class CrmGatewayOptions
{
    public const string SectionName = "Crm";

    /// <summary>
    /// Defaults to "Http" so an environment whose configuration omits the key
    /// fails closed rather than silently running fixture data; the automated
    /// test host sets "Mock" explicitly (TigerCsApiFactory).
    /// </summary>
    public string Provider { get; set; } = "Http";

    /// <summary>
    /// The legacy CRM MVC 4.7 application's base URL (e.g.
    /// <c>https://crm.tigergroup.internal/</c>), used only by
    /// <c>CrmBuyerHttpGateway</c>. Not a secret — safe to commit per
    /// environment in <c>appsettings.{Environment}.json</c>, same as
    /// <c>TigerCsApi:BaseUrl</c> in TigerCS.Web.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// The shared secret CRM validates via the <c>X-SECRET-KEY</c> request
    /// header — the same value CRM reads from
    /// <c>ConfigurationManager.AppSettings["TicketingSecretKey"]</c>. Never
    /// committed: configure via user-secrets locally or the
    /// <c>Crm__SecretKey</c> environment variable in CI/UAT/Production, per
    /// docs/DEV-SETUP.md.
    /// </summary>
    public string? SecretKey { get; set; }

    /// <summary>
    /// Extra https hosts (besides the <see cref="BaseUrl"/> origin) from which
    /// CRM document <c>fileUrl</c>s may be fetched. Empty by default: a
    /// <c>fileUrl</c> on any other host is refused. <b>The CRM secret is never
    /// sent to these hosts</b> — they are fetched anonymously (a storage host
    /// with its own signed/unguessable URLs); only the CRM origin receives
    /// <c>X-SECRET-KEY</c>. Hostnames only, e.g. <c>files.tigergroup.ae</c>.
    /// </summary>
    public List<string> DocumentFileHosts { get; set; } = [];

    /// <summary>Largest document body the gateway will read from CRM. Default 10 MB, the delivery limit.</summary>
    public int MaxDocumentBytes { get; set; } = 10 * 1024 * 1024;
}

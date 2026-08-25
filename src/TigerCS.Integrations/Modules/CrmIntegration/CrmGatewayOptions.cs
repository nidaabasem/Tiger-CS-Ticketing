namespace TigerCS.Integrations.Modules.CrmIntegration;

/// <summary>
/// The "Crm" configuration section. <see cref="Provider"/> governs which
/// <see cref="TigerCS.Application.Modules.CustomerVerification.CrmIntegration.ICrmGateway"/>
/// implementation is wired up — only "Mock" is implemented at this pilot
/// phase (MVP-Implementation-Backlog.md S-06) — no real Tiger Group CRM
/// endpoint details were available to build against for unit/contact lookup.
/// See <see cref="MockCrmGateway"/>'s own remarks: it is never
/// production-ready.
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

    public string Provider { get; set; } = "Mock";

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
}

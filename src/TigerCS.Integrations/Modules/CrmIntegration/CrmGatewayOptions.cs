namespace TigerCS.Integrations.Modules.CrmIntegration;

/// <summary>
/// Governs which <see cref="TigerCS.Application.Modules.CustomerVerification.CrmIntegration.ICrmGateway"/>
/// implementation is wired up. Only "Mock" is implemented at this pilot
/// phase (MVP-Implementation-Backlog.md S-06) — no real Tiger Group CRM
/// endpoint details were available to build against. See
/// <see cref="MockCrmGateway"/>'s own remarks: it is never production-ready.
/// </summary>
public sealed class CrmGatewayOptions
{
    public const string SectionName = "Crm";

    public string Provider { get; set; } = "Mock";
}

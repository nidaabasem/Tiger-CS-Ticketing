namespace TigerCS.Integrations.Modules.PactIntegration;

/// <summary>
/// The "Pact" configuration section — governs which
/// <see cref="TigerCS.Application.Modules.CustomerVerification.PactIntegration.IPactCustomerLookupGateway"/>
/// implementation is wired up: "Mock" (<see cref="MockPactGateway"/>,
/// fixture-backed, the default so dev/tests stay deterministic and offline)
/// or "Http" (<see cref="PactCustomerHttpGateway"/>, the real PACT
/// integration, which additionally needs the <see cref="PactApiOptions"/>
/// "PactApi" section configured). Same provider-switch shape as
/// <c>Crm:Provider</c>.
/// </summary>
public sealed class PactGatewayOptions
{
    public const string SectionName = "Pact";

    public string Provider { get; set; } = "Mock";
}

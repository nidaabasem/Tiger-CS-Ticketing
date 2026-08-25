namespace TigerCS.Integrations.Modules.PactIntegration;

/// <summary>
/// Governs which <see cref="TigerCS.Application.Modules.CustomerVerification.CustomerLookup.IPactGateway"/>
/// implementation is wired up. Only "Mock" is implemented at this pilot
/// phase — no real PACT endpoint details were available to build against.
/// See <see cref="MockPactGateway"/>'s own remarks: it is never
/// production-ready.
/// </summary>
public sealed class PactGatewayOptions
{
    public const string SectionName = "Pact";

    public string Provider { get; set; } = "Mock";
}

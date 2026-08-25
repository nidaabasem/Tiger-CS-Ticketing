namespace TigerCS.Integrations.Modules.TasleehIntegration;

/// <summary>
/// Governs which <see cref="TigerCS.Application.Modules.CustomerVerification.CustomerLookup.ITasleehGateway"/>
/// implementation is wired up. Only "Mock" is implemented at this pilot
/// phase — no real Tasleeh endpoint details were available to build
/// against. See <see cref="MockTasleehGateway"/>'s own remarks: it is never
/// production-ready.
/// </summary>
public sealed class TasleehGatewayOptions
{
    public const string SectionName = "Tasleeh";

    public string Provider { get; set; } = "Mock";
}

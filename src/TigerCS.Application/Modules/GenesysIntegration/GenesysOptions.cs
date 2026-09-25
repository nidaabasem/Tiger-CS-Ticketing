namespace TigerCS.Application.Modules.GenesysIntegration;

/// <summary>
/// The Genesys integration's own settings — a plain value in the Application
/// layer (the composition root binds configuration onto it, exactly as
/// <c>ReopenPolicy</c> and <c>OutboxDispatchPolicy</c> do, so this project
/// keeps depending on nothing but the Domain).
///
/// <para>
/// <b><see cref="Enabled"/> is the feature flag.</b> False — the default —
/// means every Genesys ingestion entry point answers "integration disabled"
/// and writes nothing; manual/Face-to-Face ticket creation, the New Ticket
/// wizard, and every existing flow behave exactly as they did before this
/// phase. Nothing in the normal ticketing path reads this flag, which is
/// what makes that guarantee structural rather than a promise.
/// </para>
///
/// <para>
/// <b>No Genesys credential or endpoint lives here.</b> Ticketing exposes
/// inbound endpoints only. Genesys Cloud authenticates with OAuth 2.0 client
/// credentials to TigerGroupWeb, which forwards to these endpoints as an
/// authenticated TigerCS service account through the system's existing JWT
/// authentication. TigerCS never calls Genesys' own API, so none of it (base
/// URL, OAuth client) is configured here.
/// </para>
/// </summary>
public sealed class GenesysOptions
{
    public const string SectionName = "Genesys";

    /// <summary>Whether inbound Genesys inquiry/end processing is switched on. Default false — the integration stays dark until it is deliberately enabled.</summary>
    public bool Enabled { get; set; }
}

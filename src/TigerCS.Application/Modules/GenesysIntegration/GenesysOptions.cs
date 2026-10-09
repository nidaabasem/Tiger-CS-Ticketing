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

    /// <summary>
    /// The TigerCS <b>Web</b> application's public base address (e.g.
    /// <c>https://tigercs-uat.example/</c>) — what a Secure Screen Pop
    /// <c>launchUrl</c> is built on, since the Api cannot know where the Web
    /// is served. Unset means Screen Pop is not configured: the endpoint
    /// refuses to issue a launch rather than guess an address.
    /// </summary>
    public string? ScreenPopWebBaseUrl { get; set; }

    /// <summary>The default for <see cref="CustomerInactivityTimeoutMinutes"/>: the business rule's "more than 5 minutes".</summary>
    public const int DefaultCustomerInactivityTimeoutMinutes = 5;

    /// <summary>
    /// How long a chatbot may wait for the customer's reply before the
    /// background job closes the ticket as an inactivity closure. Default 5;
    /// the customer must be silent for <b>more than</b> this long. Zero or a
    /// negative value switches the automatic closure off (the timer is still
    /// recorded, nothing is closed). Set it under
    /// <c>Genesys:CustomerInactivityTimeoutMinutes</c>.
    /// </summary>
    public int CustomerInactivityTimeoutMinutes { get; set; } = DefaultCustomerInactivityTimeoutMinutes;

    /// <summary>The default for <see cref="DefaultTicketPriority"/>: the business tier name used by the SLA document.</summary>
    public const string DefaultDefaultTicketPriority = "Normal";

    /// <summary>
    /// The priority a new Genesys ticket starts with, by name. Resolved
    /// against the Priorities table at ingestion time (never by a hard-coded
    /// id): a priority row with exactly this name wins, otherwise the
    /// documented business alias applies (<c>Normal</c> is the Medium tier —
    /// see <c>PriorityAliases</c>). Set it under
    /// <c>Genesys:DefaultTicketPriority</c>; blank disables the default and
    /// restores "no priority until classified".
    /// </summary>
    public string? DefaultTicketPriority { get; set; } = DefaultDefaultTicketPriority;

    /// <summary>
    /// Whether a Genesys ticket with a missing or unusable request type is put
    /// in the human follow-up queue ("awaiting classification") instead of
    /// being left for the bot alone. On by default — the business rule is that
    /// an unclassified Genesys ticket is never left without a human owner of
    /// the classification; switch it off
    /// (<c>Genesys:HumanQueueForUnclassified</c> = false) only as a rollout kill
    /// switch. The queue entry is stood down automatically when the request type
    /// is later classified.
    /// </summary>
    public bool HumanQueueForUnclassified { get; set; } = true;

    /// <summary>The configured timeout, or null when automatic closure is switched off.</summary>
    public TimeSpan? CustomerInactivityTimeout =>
        CustomerInactivityTimeoutMinutes > 0 ? TimeSpan.FromMinutes(CustomerInactivityTimeoutMinutes) : null;
}

namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// Who produced a ticket's interaction context — the explicit
/// Genesys-vs-local distinction of the Workflow/Automation architecture.
/// Interaction <i>routing</i> is Genesys's concern; Ticketing only records
/// the context it was given (or created locally) and never re-derives it.
/// </summary>
public enum InteractionContextSource : byte
{
    /// <summary>Ticketing created the context locally — today the Face-to-Face / walk-in exception, where the agent selects channel, customer phone, and department by hand. Genesys fields are always null.</summary>
    Ticketing = 1,

    /// <summary>Genesys determined the interaction context (channel, queue, agent, conversation) and handed it to Ticketing. Requires at least the conversation id; every other Genesys field is optional until the integration contract is finalized.</summary>
    Genesys = 2
}

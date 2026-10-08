namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// Why a Resolution SLA clock was paused (ISSUE-018, SLA-Architecture.md §6).
/// </summary>
public enum SlaPauseReason : byte
{
    /// <summary>The ticket is in Pending Customer (ISSUE-018b — approved).</summary>
    PendingCustomer = 1,

    /// <summary>
    /// Legacy only: the ticket is in the retired Pending Third Party status
    /// (ISSUE-018c — approved to pause). No new ticket can enter that status;
    /// the value exists so a ticket that was already in it is handled the
    /// same way rather than crashing or silently running the clock.
    /// </summary>
    PendingThirdPartyLegacy = 2
}

namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// MVP-Data-Dictionary.md §2.17 — <c>TicketEscalations.TriggerType</c>
/// ("AutoBreach/AutoWindowExpired/ManualFlag/ManualLevel4").
/// </summary>
public enum EscalationTriggerType : byte
{
    /// <summary>System-raised, the moment an SLA deadline is detected breached (FR-ESC-02, backlog S-11). Always Level 2 in this pilot — see <see cref="TicketEscalation.RaiseAutomaticLevel2OnBreach"/>.</summary>
    AutoBreach = 1,

    /// <summary>
    /// The timed Level 2 → Level 3 (GM) advance of ADR-0011/SLA-Architecture.md §12.
    /// <b>Not produced by any code path in this pilot</b> — MVP-Implementation-Backlog.md
    /// §0.2 and S-11 both place it out of scope ("the timed window-based Level 2→3
    /// auto-advance ... is not built in this pilot"), and §2.6 lists it as an
    /// explicitly-labelled stretch item. The member exists because the column's
    /// approved value set includes it, not because anything writes it.
    /// </summary>
    AutoWindowExpired = 2,

    /// <summary>An agent (or above) manually flagging the ticket for escalation — FR-ESC-01, MVP-API-Contracts.md §5.7.</summary>
    ManualFlag = 3,

    /// <summary>The Level 4 (Chairman/CEO) escalation, which is manual-only and role-gated to CS Manager/GM (Solution-Analysis.md §7.6, MVP-ERD.md §2.17, MVP-API-Contracts.md §5.7).</summary>
    ManualLevel4 = 4
}

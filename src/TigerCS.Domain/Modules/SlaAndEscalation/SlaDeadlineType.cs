namespace TigerCS.Domain.Modules.SlaAndEscalation;

/// <summary>
/// The two independent SLA deadlines ADR-0009 requires be tracked as "fully
/// independent pairs" — each with its own due timestamp, its own satisfying
/// event, and its own breach flag on every <see cref="TicketSlaInstance"/>
/// row.
///
/// <para>
/// Also the <c>CheckType</c> component of the breach-check idempotency key
/// (<c>TicketId + CheckType + DueTimestamp</c>, SLA-Architecture.md §15 /
/// ADR-0014), which is why the two deadlines need a name in the domain at
/// all rather than being implied by which property a caller reads.
/// </para>
/// </summary>
public enum SlaDeadlineType : byte
{
    /// <summary>Satisfied only by <c>Ticket.FirstHumanResponseAtUtc</c> — never by the automated acknowledgement, on any channel (ISSUE-019, SLA-Architecture.md §1).</summary>
    FirstResponse = 1,

    /// <summary>Satisfied by <c>TicketResolution.ResolvedAtUtc</c> — the moment Resolve sets the outcome. Closure is deliberately <b>not</b> the achievement event (SLA-Architecture.md §2).</summary>
    Resolution = 2
}

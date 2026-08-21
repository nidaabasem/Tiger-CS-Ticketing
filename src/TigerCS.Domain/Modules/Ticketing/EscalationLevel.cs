namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// Tiger-CS-Ticketing-Architecture-Design.md line 324 / Solution-Analysis.md
/// §5.3 / §7.6's Agent → Department Head → GM → Chairman/CEO hierarchy. One
/// of the five independent lifecycle dimensions (ADR-0008).
///
/// <para>
/// <b>Independent of <c>TicketStatus</c>, by design</b> — an escalated ticket
/// stays Open or InProgress, because escalating it "never removes it from
/// active work" (Solution-Analysis.md §7.6). <c>Ticket.RaiseEscalationLevel</c>
/// touches this dimension and no other.
/// </para>
///
/// <para>
/// <b>How each level is reached in this pilot.</b> <see cref="Level2"/>
/// automatically on an SLA breach, or manually; <see cref="Level1"/> and
/// <see cref="Level3"/> manually; <see cref="Level4"/> manually and only by a
/// CS Manager or GM (MVP-ERD.md §2.17). The timed Level 2 → Level 3 advance
/// of ADR-0011/SLA-Architecture.md §12 is not built here
/// (MVP-Implementation-Backlog.md §0.2 / S-11), so nothing advances a level
/// on a timer.
/// </para>
/// </summary>
public enum EscalationLevel : byte
{
    None = 0,
    Level1 = 1,
    Level2 = 2,
    Level3 = 3,
    Level4 = 4
}

namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// Tiger-CS-Ticketing-Architecture-Design.md line 325 / Solution-Analysis.md
/// §5.4. One of the five independent lifecycle dimensions (ADR-0008).
///
/// <para>
/// <b>A ticket-level summary of two independent clocks.</b> First Response
/// and Resolution breach independently and are tracked independently, each
/// with its own due timestamp and its own breach flag on every
/// <c>TicketSlaInstance</c> row (ADR-0009). This single column reports the
/// ticket's overall position: <see cref="Breached"/> if either clock has
/// breached (sticky, mirroring the flags' own immutability rule,
/// MVP-ERD.md §2.15), <see cref="Met"/> once the resolution landed within
/// its deadline and nothing had breached, <see cref="Running"/> otherwise.
/// </para>
///
/// <para>
/// <b><see cref="Paused"/> has exactly one producer in this pilot</b>, and it
/// is not the pause/resume feature: a provisional ticket created during a CRM
/// outage carries it because FR-TKT-09 says an unverified ticket "does not
/// start its SLA clock", and it becomes <see cref="Running"/> at
/// reconciliation. SLA pause/resume proper — <c>TicketSlaPausePeriods</c>,
/// the PendingCustomer/PendingThirdParty clock stop — is explicitly not built
/// in this pilot (MVP-Implementation-Backlog.md §0.2, a disclosed
/// limitation), so no status change pauses a running clock here.
/// </para>
/// </summary>
public enum SlaState : byte
{
    Running = 1,
    Paused = 2,
    Met = 3,
    Breached = 4,
    NotApplicable = 5
}

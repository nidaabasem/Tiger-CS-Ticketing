namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// Tiger-CS-Ticketing-Architecture-Design.md line 325 / Solution-Analysis.md
/// §5.4. One of the five independent lifecycle dimensions (ADR-0008). No due
/// date is computed and no breach detection runs in this increment (SLA and
/// Escalation is a later module, MVP-Implementation-Backlog.md S-09/S-10) —
/// only the column's already-defined value set is used, to set a
/// schema-correct initial value at ticket creation:
/// <see cref="Paused"/> for a provisional ticket (its clock has not started —
/// FR-TKT-09, "not ... SLA-clocked" while unverified) and <see cref="Running"/>
/// once <see cref="Ticket.VerificationStatus"/> is <see cref="CrmVerificationStatus.Verified"/>.
/// </summary>
public enum SlaState : byte
{
    Running = 1,
    Paused = 2,
    Met = 3,
    Breached = 4,
    NotApplicable = 5
}

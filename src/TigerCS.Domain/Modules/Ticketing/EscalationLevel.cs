namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>Tiger-CS-Ticketing-Architecture-Design.md line 324 / Solution-Analysis.md §5.3. One of the five independent lifecycle dimensions (ADR-0008). Not advanced by any code path in this increment — SLA and Escalation is a later module.</summary>
public enum EscalationLevel : byte
{
    None = 0,
    Level1 = 1,
    Level2 = 2,
    Level3 = 3,
    Level4 = 4
}

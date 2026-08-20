namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>MVP-Data-Dictionary.md §2.13 — which of the five independent dimensions (ADR-0008) a <see cref="TicketStatusHistory"/> row describes.</summary>
public enum TicketStatusDimension : byte
{
    TicketStatus = 1,
    VerificationStatus = 2,
    EscalationLevel = 3,
    SlaState = 4,
    ResolutionOutcome = 5
}

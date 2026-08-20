namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>Tiger-CS-Ticketing-Architecture-Design.md line 322. One of the five independent lifecycle dimensions (ADR-0008).</summary>
public enum TicketStatus : byte
{
    Open = 1,
    InProgress = 2,
    PendingCustomer = 3,
    PendingThirdParty = 4,
    Resolved = 5,
    Closed = 6
}

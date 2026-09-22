namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// Tiger-CS-Ticketing-Architecture-Design.md line 322. One of the five
/// independent lifecycle dimensions (ADR-0008).
///
/// <para>
/// <b>Values are never renumbered.</b> They are persisted as the
/// <c>Tickets.TicketStatus</c> tinyint and as the old/new values of every
/// <c>TicketStatusHistory</c> row, so the numbering here is a storage
/// contract with existing data, not an implementation detail.
/// </para>
/// </summary>
public enum TicketStatus : byte
{
    Open = 1,
    InProgress = 2,
    PendingCustomer = 3,

    /// <summary>
    /// <b>Legacy readable status — never a transition target.</b> Retired
    /// from the active lifecycle by the approved lifecycle cleanup: no ticket
    /// can enter it any more (see
    /// <see cref="TicketStatusTransitions.IsChangeStatusAllowed"/>, which has
    /// no arm producing it), and it is offered in no status picker or
    /// operational filter. The value stays at 4 — deleting or renumbering it
    /// would silently reinterpret every historical ticket, status-history row
    /// and pending record that already carries it. Tickets still in it remain
    /// fully readable and return to <see cref="InProgress"/> through the
    /// ordinary Change Status operation.
    /// </summary>
    PendingThirdParty = 4,

    Resolved = 5,
    Closed = 6
}

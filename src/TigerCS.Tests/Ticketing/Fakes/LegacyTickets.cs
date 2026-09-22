using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.Ticketing.Fakes;

/// <summary>
/// Materializes a ticket in <see cref="TicketStatus.PendingThirdParty"/> —
/// the legacy, no-longer-reachable status — the only way it can still exist:
/// as a row that was already in it when the approved lifecycle cleanup
/// retired it.
///
/// <para>
/// <b>Deliberately written straight onto the property, exactly as EF Core
/// rehydrates a stored row</b>, never through
/// <see cref="Ticket.ChangeStatus"/>. There is no transition into this status
/// any more, and that is precisely the property under test — a helper that
/// could reach it through the domain would mean the cleanup had not happened.
/// The same reflection-onto-a-private-setter pattern the other fakes here use
/// for identity columns.
/// </para>
/// </summary>
public static class LegacyTickets
{
    /// <summary>
    /// Forces <paramref name="ticket"/> into the legacy PendingThirdParty
    /// status, as though it had been read back from a historical row, and
    /// returns it for chaining.
    /// </summary>
    public static Ticket AsLegacyPendingThirdParty(this Ticket ticket)
    {
        typeof(Ticket)
            .GetProperty(nameof(Ticket.TicketStatus))!
            .SetValue(ticket, TicketStatus.PendingThirdParty);

        return ticket;
    }
}

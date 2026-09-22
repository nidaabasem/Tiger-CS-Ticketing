namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// The single authoritative <c>TicketStatus</c> transition table for the
/// "work" sub-machine <see cref="Ticket.ChangeStatus"/> owns, plus the one
/// place that names which statuses are still operationally selectable.
///
/// <para>
/// <b>Why it lives here rather than inside <see cref="Ticket"/>.</b> The
/// Ticket Details status picker must offer exactly the targets the domain
/// would accept, and the Dashboard/queue filters must offer exactly the
/// statuses the business still operates. Both are display concerns that read
/// this table, so a UI list and the domain rule can never drift into
/// disagreement — which is what let the picker offer <c>Open</c>,
/// <c>PendingThirdParty</c> and (before Resolve/Close were split out) more
/// besides, every one of which the domain already refused.
/// </para>
///
/// <para>
/// <b><see cref="TicketStatus.PendingThirdParty"/> is a legacy readable
/// status.</b> It was an active status before the approved lifecycle
/// cleanup and historical tickets, status-history rows and pending records
/// still carry it, so its enum value stays exactly where it is (4) and every
/// read path keeps working. What changed is that no transition targets it any
/// more: it is reachable only by tickets that entered it before the change,
/// and their single sanctioned exit is back to
/// <see cref="TicketStatus.InProgress"/>.
/// </para>
/// </summary>
public static class TicketStatusTransitions
{
    /// <summary>
    /// Statuses a new ticket may still be moved into, be filtered by as an
    /// operational choice, or be configured against — everything except the
    /// legacy-only ones. Historical tickets still <i>display</i> their real
    /// stored status (history is never rewritten); this list governs the
    /// pickers, not the rendering.
    /// </summary>
    public static readonly IReadOnlyList<TicketStatus> ActiveStatuses =
        [.. Enum.GetValues<TicketStatus>().Where(s => !IsLegacyOnly(s))];

    /// <summary>
    /// True for a status that exists only so historical rows remain readable.
    /// Such a status is never a transition target and never an active picker
    /// option, but is always a legal value to read back out of the database.
    /// </summary>
    public static bool IsLegacyOnly(TicketStatus status) => status is TicketStatus.PendingThirdParty;

    /// <summary>
    /// Solution-Analysis.md §5.6's transition table, restricted to the "work"
    /// sub-machine: Open→InProgress, InProgress↔PendingCustomer, and the
    /// legacy PendingThirdParty→InProgress escape. Resolved and Closed are
    /// reached only through <see cref="Ticket.Resolve"/>/<see cref="Ticket.Close"/>,
    /// and Closed returns to InProgress only through <see cref="Ticket.Reopen"/>
    /// — all three stay dedicated lifecycle operations rather than generic
    /// status changes, so none of them appears here.
    /// </summary>
    public static bool IsChangeStatusAllowed(TicketStatus from, TicketStatus to) => (from, to) switch
    {
        (TicketStatus.Open, TicketStatus.InProgress) => true,
        (TicketStatus.InProgress, TicketStatus.PendingCustomer) => true,
        (TicketStatus.PendingCustomer, TicketStatus.InProgress) => true,

        // Legacy compatibility only — the escape path for tickets that were
        // already PendingThirdParty when it stopped being an active status.
        // There is deliberately no arm producing PendingThirdParty: that is
        // what makes "no new ticket may enter it" a property of the table
        // rather than a check someone has to remember to write.
        (TicketStatus.PendingThirdParty, TicketStatus.InProgress) => true,

        _ => false
    };

    /// <summary>
    /// Every status <see cref="Ticket.ChangeStatus"/> would accept from
    /// <paramref name="current"/>, in enum order. Empty for a status with no
    /// generic next step (Resolved, Closed) — the caller hides the Change
    /// Status action entirely rather than offering an empty picker.
    /// </summary>
    public static IReadOnlyList<TicketStatus> AllowedTargetsFrom(TicketStatus current) =>
        [.. Enum.GetValues<TicketStatus>().Where(target => IsChangeStatusAllowed(current, target))];

    /// <summary>
    /// <see cref="AllowedTargetsFrom(TicketStatus)"/> for a status that
    /// arrived as a wire string (an Api DTO's <c>TicketStatus</c>). An
    /// unrecognized value yields no targets — an unknown status is never
    /// guessed into a transition.
    /// </summary>
    public static IReadOnlyList<TicketStatus> AllowedTargetsFrom(string? current) =>
        Enum.TryParse<TicketStatus>(current, ignoreCase: true, out var parsed)
            ? AllowedTargetsFrom(parsed)
            : [];
}

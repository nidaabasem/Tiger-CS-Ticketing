namespace TigerCS.Web.Authorization;

/// <summary>
/// Which shell destinations a user is offered.
///
/// <para>
/// The queue is the only destination every authenticated staff member can
/// reach; "New ticket" is offered to the roles the API's
/// <c>CustomerVerification</c> policy admits. Nothing else in this increment
/// has a screen, so nothing else appears - a nav item that leads nowhere is
/// worse than an absent one.
/// </para>
///
/// <para>
/// As with <see cref="TicketActionPolicy"/>, this decides what is <i>offered</i>,
/// never what is <i>permitted</i>. Navigating directly to a page a user should
/// not have still ends in the API's own 401/403, which the pages render.
/// </para>
/// </summary>
public static class NavigationPolicy
{
    /// <summary>Every authenticated staff member has a queue; its contents are scoped server-side.</summary>
    public static bool CanViewQueue(UserContext user) => user.IsAuthenticated;

    /// <summary>Intake, CRM lookup, verification and creation are CS Agent / CS Supervisor (and System Administrator).</summary>
    public static bool CanStartNewTicket(UserContext user) =>
        user.IsAuthenticated && TicketActionPolicy.CanCreateTickets(user);
}

namespace TigerCS.Web.Models;

/// <summary>Presentation helpers shared by the Tickets and Ticket Details views.</summary>
public static class TicketDisplay
{
    public static string StatusLabel(TicketStatus status) => status switch
    {
        TicketStatus.Open => "Open",
        TicketStatus.InProgress => "In Progress",
        TicketStatus.AwaitingCustomer => "Awaiting Customer",
        TicketStatus.Closed => "Closed",
        _ => status.ToString()
    };

    public static string StatusCssKey(TicketStatus status) => status switch
    {
        TicketStatus.Open => "open",
        TicketStatus.InProgress => "inprogress",
        TicketStatus.AwaitingCustomer => "awaitingcustomer",
        TicketStatus.Closed => "closed",
        _ => "open"
    };

    public static string PriorityLabel(TicketPriority priority) => priority switch
    {
        TicketPriority.Critical => "Critical",
        TicketPriority.High => "High",
        TicketPriority.Medium => "Medium",
        TicketPriority.Low => "Low",
        _ => priority.ToString()
    };

    public static string PriorityCssKey(TicketPriority priority) => priority.ToString().ToLowerInvariant();

    public static string SlaCssKey(SlaState sla) => sla switch
    {
        SlaState.OnTrack => "ontrack",
        SlaState.DueSoon => "duesoon",
        SlaState.Breached => "breached",
        _ => "ontrack"
    };
}

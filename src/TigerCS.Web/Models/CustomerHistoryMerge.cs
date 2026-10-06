using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Web.Models;

/// <summary>
/// Combines the ticket histories of one customer's two verified identities
/// — the Tiger CRM Buyer id and the PACT tenant id — into one view for the
/// unified New Ticket customer card. Each history is read by its own stable
/// identity exactly as before (never by name or phone); only the display is
/// combined. Rows are de-duplicated by <c>TicketId</c> (a ticket persists
/// with either a CRM Buyer identity or an external identity, never both,
/// so the two reads are disjoint by construction — the de-duplication is a
/// guarantee, not an expectation), ordered active-first then newest, and
/// bounded to <paramref name="limit"/>. Counts are summed and reduced by
/// any duplicate that did surface across the two pages.
/// </summary>
public static class CustomerHistoryMerge
{
    public static CustomerHistoryDto Combine(CustomerHistoryDto crm, CustomerHistoryDto external, int limit)
    {
        var seen = new HashSet<long>();
        var merged = new List<CustomerHistoryTicketDto>();
        var duplicates = new List<CustomerHistoryTicketDto>();
        foreach (var ticket in crm.Tickets.Concat(external.Tickets))
        {
            if (seen.Add(ticket.TicketId))
            {
                merged.Add(ticket);
            }
            else
            {
                duplicates.Add(ticket);
            }
        }

        var ordered = merged
            .OrderBy(t => IsFinished(t) ? 1 : 0)
            .ThenByDescending(t => t.CreatedAtUtc)
            .Take(limit)
            .ToList();

        var duplicateOpen = duplicates.Count(t => !IsFinished(t));
        var duplicateClosed = duplicates.Count - duplicateOpen;

        return new CustomerHistoryDto(
            "Verified",
            crm.CrmBuyerCustomerId,
            PhoneNumberSnapshot: null,
            crm.CustomerDisplayName ?? external.CustomerDisplayName,
            TotalTickets: crm.TotalTickets + external.TotalTickets - duplicates.Count,
            OpenTickets: crm.OpenTickets + external.OpenTickets - duplicateOpen,
            ClosedTickets: crm.ClosedTickets + external.ClosedTickets - duplicateClosed,
            ordered,
            external.ExternalSource,
            external.ExternalCustomerId,
            crm.CustomerKey ?? external.CustomerKey);
    }

    private static bool IsFinished(CustomerHistoryTicketDto ticket) => ticket.TicketStatus is "Resolved" or "Closed";
}

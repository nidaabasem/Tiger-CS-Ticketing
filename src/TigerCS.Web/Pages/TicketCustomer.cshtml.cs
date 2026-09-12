using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages;

/// <summary>
/// `/Tickets/{ticketId}/Customer` — the address the Customer Profile used to
/// live at, kept as a resolver: it works out which customer the ticket
/// belongs to (the Api's customer-history read reports the directory key:
/// CRM Buyer id, else external identity, else intake phone) and redirects to
/// that customer's profile under <c>/Customers</c>. A ticket with no customer
/// identity at all goes back to its Ticket Details, whose Customer tab says
/// so.
/// </summary>
public sealed class TicketCustomerModel(TicketsApiClient ticketsApiClient) : PageModel
{
    public async Task<IActionResult> OnGetAsync(long ticketId, CancellationToken cancellationToken)
    {
        var history = await ticketsApiClient.GetCustomerHistoryAsync(ticketId, limit: 1, cancellationToken);
        if (history.Outcome == ApiOutcome.NotFound)
        {
            return NotFound();
        }

        var key = history.IsSuccess ? history.Value?.CustomerKey : null;
        if (key is null)
        {
            // The history read failed or found no identity — fall back to the
            // ticket's own persisted facts before giving up.
            var detail = await ticketsApiClient.GetByIdAsync(ticketId, cancellationToken);
            if (detail.Outcome == ApiOutcome.NotFound)
            {
                return NotFound();
            }

            key = detail.Value is { } t
                ? CustomerIdentity.FromTicketFacts(t.CrmBuyerCustomerId, t.CustomerVerificationSource, t.ExternalCustomerId, null)?.Key
                : null;
        }

        return key is null
            ? LocalRedirect($"/Tickets/{ticketId}#customer")
            : LocalRedirect($"/Customers/{Uri.EscapeDataString(key)}?fromTicket={ticketId}");
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Web.Services;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages;

/// <summary>
/// Customer Details/Profile — ticket-anchored (<c>/Tickets/{ticketId}/Customer</c>,
/// linked from Ticket Details' Verification &amp; Unit panel), reached the
/// same way Customer History already is: the identity
/// (<c>CrmBuyerCustomerId</c>) and the authorization check both come from
/// the ticket, never from a phone number or customer name typed anywhere on
/// this page.
///
/// <para>
/// Overview/Contact Info/Units come from <see cref="TicketsApiClient.GetCustomerProfileAsync"/>
/// (live CRM, via the reused CrmBuyerLookupAppService — no CRM logic
/// duplicated here). Previous Tickets reuses
/// <see cref="TicketsApiClient.GetCustomerHistoryAsync"/> unchanged — the
/// exact same endpoint Ticket Details' own Previous Tickets tab calls.
/// </para>
/// </summary>
public sealed class CustomerProfileModel(TicketsApiClient ticketsApiClient, TicketNameResolver nameResolver) : PageModel
{
    /// <summary>A dedicated page's Previous Tickets tab shows more than the compact Ticket Details preview — still bounded, never unlimited.</summary>
    private const int PreviousTicketsLimit = 20;

    public long TicketId { get; private set; }
    public ApiOutcome Outcome { get; private set; }
    public CustomerProfileDto? Profile { get; private set; }
    public CustomerHistoryDto? History { get; private set; }
    public TicketNameResolver NameResolver => nameResolver;

    public async Task<IActionResult> OnGetAsync(long ticketId, CancellationToken cancellationToken)
    {
        TicketId = ticketId;
        await nameResolver.PrimeOwnDepartmentsAsync(cancellationToken);

        var profileTask = ticketsApiClient.GetCustomerProfileAsync(ticketId, cancellationToken);
        var historyTask = ticketsApiClient.GetCustomerHistoryAsync(ticketId, PreviousTicketsLimit, cancellationToken);
        await Task.WhenAll(profileTask, historyTask);

        Outcome = profileTask.Result.Outcome;
        Profile = profileTask.Result.IsSuccess ? profileTask.Result.Value : null;
        History = historyTask.Result.IsSuccess ? historyTask.Result.Value : null;

        if (Profile is null && Outcome == ApiOutcome.NotFound)
        {
            return NotFound();
        }

        return Page();
    }
}

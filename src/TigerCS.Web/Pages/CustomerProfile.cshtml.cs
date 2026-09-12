using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Web.Models;
using TigerCS.Web.Services;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;

namespace TigerCS.Web.Pages;

/// <summary>
/// The Customer Profile (`/Customers/{customerKey}`): a CRM-style workspace
/// for one customer identity — header (name, phone, verification source,
/// primary unit), then Overview / Contact Info / Units / Tickets /
/// Interactions. Everything comes from what TigerCS itself persisted about
/// the customer (the Customers directory profile: tickets, units, phones,
/// interactions); for a CRM-verified customer the Contact Info and Units tabs
/// are enriched with the live CRM Buyer record through the existing
/// ticket-anchored customer-profile endpoint — the same read the New Ticket
/// wizard relies on, never a new CRM API.
/// </summary>
public sealed class CustomerProfileModel(
    CustomersApiClient customersApiClient,
    TicketsApiClient ticketsApiClient,
    TicketNameResolver nameResolver) : PageModel
{
    public string CustomerKey { get; private set; } = string.Empty;
    public ApiOutcome Outcome { get; private set; }
    public CustomerDirectoryProfileDto? Profile { get; private set; }

    /// <summary>Live CRM Buyer details for a CRM-verified customer — null when the customer is not CRM-verified or the call failed (the page then says so, and still shows everything persisted).</summary>
    public CustomerProfileDto? CrmProfile { get; private set; }

    /// <summary>Where "Back to Customers" leads: the remembered directory list (search, filters, page), or the bare directory.</summary>
    public string CustomersHref { get; private set; } = CustomersContext.BasePath;

    /// <summary>The ticket the agent came from, when they arrived from Ticket Details — for a "Back to ticket" link.</summary>
    public long? FromTicketId { get; private set; }
    public string? FromTicketNumber { get; private set; }

    public TicketNameResolver NameResolver => nameResolver;
    public CurrentUser? Viewer { get; private set; }
    public bool CanCreateTicket { get; private set; }
    public bool ViewerCanReopen => TicketActions.CanReopen(Viewer?.Roles);

    public string DisplayName =>
        Profile is null ? "Customer"
        : !string.IsNullOrWhiteSpace(Profile.DisplayName) ? Profile.DisplayName
        : CrmProfile?.FullNameEnglish ?? CrmProfile?.FullNameArabic
        ?? (Profile.IdentityKind == "Phone" ? "Unnamed caller" : $"{CustomersModel.SourceLabel(Profile.VerificationSource)} customer");

    public string? PrimaryPhone => Profile?.PhoneNumbers.FirstOrDefault() ?? CrmProfile?.MobileNumber;

    /// <summary>The unit the customer's most recent ticket was raised for.</summary>
    public CustomerDirectoryUnitDto? PrimaryUnit => Profile?.Units.FirstOrDefault();

    /// <summary>The New Ticket wizard, with this customer's phone carried forward so the lookup step is pre-filled.</summary>
    public string NewTicketHref =>
        PrimaryPhone is { } phone ? $"/NewTicket?phoneNumber={Uri.EscapeDataString(phone)}" : "/NewTicket";

    public async Task<IActionResult> OnGetAsync(string customerKey, long? fromTicket, CancellationToken cancellationToken)
    {
        CustomerKey = customerKey;
        Viewer = CurrentUser.FromPrincipal(User);
        CanCreateTicket = TicketCreationPolicy.AppliesTo(Viewer);
        CustomersHref = CustomersContext.HrefFromCookieValue(Request.Cookies[CustomersContext.CookieName]);
        FromTicketId = fromTicket;

        await nameResolver.PrimeDepartmentsAsync(cancellationToken);

        var result = await customersApiClient.GetProfileAsync(customerKey, cancellationToken);
        Outcome = result.Outcome;
        if (!result.IsSuccess || result.Value is null)
        {
            return Outcome is ApiOutcome.NotFound or ApiOutcome.ValidationError ? NotFound() : Page();
        }

        Profile = result.Value;
        FromTicketNumber = FromTicketId is { } fromId ? Profile.Tickets.FirstOrDefault(t => t.TicketId == fromId)?.TicketNumber : null;

        if (Profile.IdentityKind == "Crm")
        {
            // Live CRM details, anchored on the customer's latest ticket (the
            // endpoint verifies the CRM record is this ticket's own customer).
            var crm = await ticketsApiClient.GetCustomerProfileAsync(Profile.LastTicketId, cancellationToken);
            CrmProfile = crm.IsSuccess ? crm.Value : null;
        }

        return Page();
    }

    public static string SourceLabel(string verificationSource) => CustomersModel.SourceLabel(verificationSource);

    public static string SourceCssKey(string verificationSource) => CustomersModel.SourceCssKey(verificationSource);

    /// <summary>True while a ticket is in a non-terminal status.</summary>
    public static bool IsActive(string ticketStatus) => ticketStatus is "Open" or "InProgress" or "PendingCustomer" or "PendingThirdParty";
}

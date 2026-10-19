using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Application.Modules.GenesysIntegration.Dto;

/// <summary>
/// One of the caller's existing tickets, as the call-pickup lookup reports
/// it. Just enough for an agent to see "this person already has an open NOC
/// request" before the conversation starts — the full ticket is one call
/// away at <c>GET /api/tickets/{ticketId}</c>.
/// </summary>
/// <param name="TicketId">The ticket.</param>
/// <param name="TicketNumber">Its human-facing number.</param>
/// <param name="TicketStatus">One of Open, InProgress, PendingCustomer, Resolved, Closed — or the legacy, no-longer-reachable PendingThirdParty on a historical ticket.</param>
/// <param name="IsOpen">Whether it is still being worked — the field that matters on a call pickup.</param>
/// <param name="RequestSummary">The one-line summary.</param>
/// <param name="CurrentDepartmentId">The department currently holding it.</param>
/// <param name="CreatedAtUtc">When it was created.</param>
public sealed record GenesysCustomerTicketDto(
    long TicketId,
    string TicketNumber,
    string TicketStatus,
    bool IsOpen,
    string RequestSummary,
    int CurrentDepartmentId,
    DateTime CreatedAtUtc);

/// <summary>
/// What a Genesys call-pickup lookup answers.
///
/// <para>
/// <b>Context, not a yes/no.</b> The agent picking up needs to know who is
/// calling and what they already have open, so this returns the customer,
/// their units and their existing tickets — not a boolean.
/// </para>
///
/// <para>
/// <b>Never a gate.</b> <see cref="Found"/> being false is a perfectly normal
/// answer, and it never stops the following Create Ticket call. Nor does a
/// source being down: <see cref="CrmStatus"/> reports Failed alongside
/// whatever the other sources did return.
/// </para>
/// </summary>
/// <param name="PhoneNumber">The number that was searched — the caller's number after telephony normalization ("tel:+971…" is searched as "+971…"), or empty when the address carried no number at all (a withheld caller id).</param>
/// <param name="Found">True when at least one source matched a customer. False is a clean, expected answer.</param>
/// <param name="CrmStatus">The CRM Buyer Lookup outcome: Found, NotFound, AmbiguousMatch or Failed — or NotSearched when there was no number to search with.</param>
/// <param name="CrmBuyers">The matched CRM Buyer (at most one) with every eligible unit, when CRM matched.</param>
/// <param name="ExternalSources">The PACT and Tasleeh results, each Found/NotFound/Failed with its matched customers and units.</param>
/// <param name="Tickets">The caller's existing tickets, newest first — open ones first within that. Empty when none are found or none are visible.</param>
/// <param name="OpenTicketCount">How many of the caller's tickets are still being worked.</param>
/// <param name="ScreenPop">The same answer flattened for a contact-center screen pop — see <see cref="GenesysScreenPopDto"/>.</param>
public sealed record GenesysCustomerLookupResultDto(
    string PhoneNumber,
    bool Found,
    string CrmStatus,
    IReadOnlyList<CrmBuyerMatchDto> CrmBuyers,
    IReadOnlyList<CustomerLookupSourceResultDto> ExternalSources,
    IReadOnlyList<GenesysCustomerTicketDto> Tickets,
    int OpenTicketCount,
    GenesysScreenPopDto ScreenPop)
{
    /// <summary>
    /// Additive. Only meaningful when the lookup was given the current Genesys <c>conversationId</c>:
    /// the customer's most recent ticket <i>other than</i> the one this conversation created
    /// (newest <c>CreatedAt</c> first, manual or Genesys-created alike). Null when there is none.
    /// </summary>
    public GenesysCustomerTicketDto? LatestPreviousTicket { get; init; }

    /// <summary>Additive. True when a <c>conversationId</c> was supplied and the customer has no ticket besides the current conversation's.</summary>
    public bool NoPreviousTicketFound { get; init; }
}

/// <summary>
/// The lookup reduced to flat, display-ready values — what a Genesys Data
/// Action maps straight onto Architect variables and participant data, which
/// cannot reliably pick a value out of the nested per-source arrays.
///
/// <para>
/// <b>A projection, never a second answer.</b> Every value is derived from
/// the full result beside it, in a fixed order — CRM first (the verified
/// Buyer record), then PACT, then Tasleeh — so the screen pop and the full
/// result can never disagree. It never picks between two different
/// customers: when the sources matched more than one,
/// <see cref="GenesysScreenPopDto.CustomerName"/> is the first in that order
/// and <see cref="GenesysScreenPopDto.MatchedCustomerCount"/> says there
/// were others, leaving identification to the agent exactly as the New
/// Ticket wizard does.
/// </para>
///
/// <para>
/// Every string is empty rather than null when there is nothing to show, so
/// a data action's output contract can declare them all as plain strings.
/// </para>
/// </summary>
/// <param name="CustomerName">The matched customer's display name, or empty.</param>
/// <param name="CustomerEmail">That customer's email, or empty.</param>
/// <param name="VerificationSource">Which source identified the customer: "Crm", "Pact" or "Tasleeh" — empty when none did.</param>
/// <param name="ExternalCustomerId">The customer's id in that source, or empty.</param>
/// <param name="MatchedCustomerCount">How many customer records the sources matched in total. More than one means the agent must confirm who is calling.</param>
/// <param name="Units">Every unit found for the caller across all sources, as "Project - Unit" labels, de-duplicated.</param>
/// <param name="UnitsText">The same labels joined with "; " — one string, for a display field that cannot show a list.</param>
/// <param name="RecentTicketNumbers">The caller's recent ticket numbers, in <see cref="GenesysCustomerLookupResultDto.Tickets"/> order.</param>
/// <param name="OpenTicketNumbers">The subset still being worked.</param>
/// <param name="RecentTicketsText">The recent tickets as one display string ("TG-… (InProgress); TG-… (Closed)").</param>
/// <param name="SelectableUnits">Additive. The same de-duplicated units as <paramref name="Units"/>, structured: display label plus the resolved PACT unit code. Expired PACT contracts are never listed. Same order and count as <paramref name="Units"/>.</param>
public sealed record GenesysScreenPopDto(
    string CustomerName,
    string CustomerEmail,
    string VerificationSource,
    string ExternalCustomerId,
    int MatchedCustomerCount,
    IReadOnlyList<string> Units,
    string UnitsText,
    IReadOnlyList<string> RecentTicketNumbers,
    IReadOnlyList<string> OpenTicketNumbers,
    string RecentTicketsText,
    IReadOnlyList<GenesysSelectableUnitDto>? SelectableUnits = null)
{
    /// <summary>Never null on the wire: an absent list is an empty one.</summary>
    public IReadOnlyList<GenesysSelectableUnitDto> SelectableUnits { get; init; } = SelectableUnits ?? [];

    /// <summary>Additive. Number of the customer's latest ticket excluding the current conversation's own, or empty (also empty when no conversationId was supplied).</summary>
    public string LatestPreviousTicketNumber { get; init; } = string.Empty;

    /// <summary>Additive. Status of that ticket, or empty.</summary>
    public string LatestPreviousTicketStatus { get; init; } = string.Empty;

    /// <summary>Additive. One-line summary of that ticket, or empty.</summary>
    public string LatestPreviousTicketSummary { get; init; } = string.Empty;

    /// <summary>Additive. A ready-to-speak sentence: the previous ticket, or "no previous ticket was found". Empty when no conversationId was supplied.</summary>
    public string LatestPreviousTicketMessage { get; init; } = string.Empty;
}

/// <summary>
/// One unit the agent can pick on the screen pop. Selection alone proves
/// nothing about who is calling: a shared phone number is not verified
/// identity, so payment details still need the verification flow.
/// </summary>
/// <param name="Label">The display label ("Project - Unit"); the CRM label when CRM and PACT both confirm the unit by resolved unit code.</param>
/// <param name="UnitCode">The resolved PACT unit code (<c>TP130-902-B</c>), or empty when it could not be resolved — never guessed.</param>
/// <param name="PaymentLookupReady">True only when <paramref name="UnitCode"/> is resolved — a statement about the code, nothing more. It does not mean ownership, the tenant or the caller has been verified; that still needs the verification flow. False means the unit is shown for the agent but must not be sent to a payment lookup.</param>
/// <param name="Sources">Which sources confirm the unit: "Crm" and/or "Pact".</param>
/// <param name="Customers">Every matched customer record this unit was found under. The unit is listed once, but the people are never merged: two customers sharing a phone number both appear here, each under their own source and id, matching <c>screenPop</c>'s customer records.</param>
public sealed record GenesysSelectableUnitDto(
    string Label,
    string UnitCode,
    bool PaymentLookupReady,
    IReadOnlyList<string> Sources,
    IReadOnlyList<GenesysUnitCustomerRefDto> Customers)
{
    /// <summary><see cref="Sources"/> as one string (<c>Crm,Pact</c>) — the flat form the Data Action maps, since Architect cannot take nested arrays.</summary>
    public string SourcesText => string.Join(",", Sources);

    /// <summary><see cref="Customers"/> as one string (<c>Crm:4001;Pact:T1</c>, <c>Source:ExternalCustomerId</c> joined with ';') — the flat form the Data Action maps. Never empty: a unit is always listed under at least one record.</summary>
    public string CustomersText => string.Join(";", Customers.Select(c => $"{c.Source}:{c.ExternalCustomerId}"));
}

/// <summary>A reference to one matched customer record (<c>verificationSource</c>/<c>externalCustomerId</c> style) that a unit was found under. A reference only: it says the source listed the unit for this customer, not that the caller is this customer.</summary>
/// <param name="Source">"Crm", "Pact" or "Tasleeh".</param>
/// <param name="ExternalCustomerId">The customer's id in that source.</param>
public sealed record GenesysUnitCustomerRefDto(string Source, string ExternalCustomerId);

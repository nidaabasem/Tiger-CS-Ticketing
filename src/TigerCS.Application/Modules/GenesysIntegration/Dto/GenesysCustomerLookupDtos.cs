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
/// <param name="TicketStatus">Open, InProgress, PendingCustomer, PendingThirdParty, Resolved or Closed.</param>
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
/// <param name="PhoneNumber">The number that was searched, echoed back.</param>
/// <param name="Found">True when at least one source matched a customer. False is a clean, expected answer.</param>
/// <param name="CrmStatus">The CRM Buyer Lookup outcome: Found, NotFound, AmbiguousMatch or Failed.</param>
/// <param name="CrmBuyers">The matched CRM Buyer (at most one) with every eligible unit, when CRM matched.</param>
/// <param name="ExternalSources">The PACT and Tasleeh results, each Found/NotFound/Failed with its matched customers and units.</param>
/// <param name="Tickets">The caller's existing tickets, newest first — open ones first within that. Empty when none are found or none are visible.</param>
/// <param name="OpenTicketCount">How many of the caller's tickets are still being worked.</param>
public sealed record GenesysCustomerLookupResultDto(
    string PhoneNumber,
    bool Found,
    string CrmStatus,
    IReadOnlyList<CrmBuyerMatchDto> CrmBuyers,
    IReadOnlyList<CustomerLookupSourceResultDto> ExternalSources,
    IReadOnlyList<GenesysCustomerTicketDto> Tickets,
    int OpenTicketCount);

using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// The call-pickup lookup: an agent has just answered, and Genesys asks who
/// is on the line.
///
/// <para>
/// <b>Pure composition — no second CRM integration.</b> The customer identity
/// comes from <see cref="CustomerSearchAppService"/>, the exact service the
/// New Ticket wizard and the Customer Workspace call, which in turn wraps the
/// existing CRM Buyer Lookup and the PACT/Tasleeh gateways. Genesys never
/// reaches Tiger CRM, PACT or Tasleeh directly, and no gateway, mapping or
/// verification rule is duplicated here — so this endpoint can never disagree
/// with the wizard about who a customer is.
/// </para>
///
/// <para>
/// <b>The ticket context is the existing phone-keyed lookup</b> that
/// <see cref="CustomerHistoryAppService"/>'s unverified path already uses:
/// intake records carry the number the customer called from, and the linked
/// ticket ids come straight off them. Nothing new is queried, and no
/// phone-to-customer identity is invented — these are simply "tickets that
/// arrived from this number".
/// </para>
///
/// <para>
/// <b>Never a gate.</b> Not finding a customer is a normal answer, not an
/// error, and neither it nor a source outage stops the Create Ticket call
/// that follows. That is the same enrichment-never-a-gate rule ticket
/// creation itself applies.
/// </para>
///
/// <para>
/// Read-only and side-effect free: no intake record, no ticket, nothing
/// persisted. A ringing call still creates nothing anywhere.
/// </para>
/// </summary>
public sealed class GenesysCustomerLookupAppService(
    GenesysOptions options,
    CustomerSearchAppService customerSearchAppService,
    IIntakeRecordRepository intakeRecordRepository,
    ITicketRepository ticketRepository)
{
    /// <summary>How many of the caller's recent tickets are returned. Bounded so a frequent caller cannot make a call-pickup lookup slow.</summary>
    private const int TicketLimit = 10;

    /// <summary>Null when the Genesys integration is switched off — the caller is answered 503, exactly as every other Genesys endpoint is.</summary>
    public async Task<GenesysCustomerLookupResultDto?> LookUpAsync(
        string phoneNumber, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return null;
        }

        var trimmed = phoneNumber.Trim();

        var search = await customerSearchAppService.SearchByPhoneAsync(trimmed, cancellationToken);

        var found = search.CrmBuyers.Count > 0
            || search.ExternalSources.Any(s => s.Customers.Count > 0);

        // Tickets that arrived from this number. Deliberately NOT scoped to
        // one caller's visible departments: Genesys calls as a service
        // account on behalf of whichever agent picked up, and an agent
        // answering a call needs to know the customer has an open case even
        // when it sits in a department they cannot open themselves. The
        // summary is deliberately thin for that reason — no customer data
        // beyond what this caller's own number already produced.
        var linkedTicketIds = await intakeRecordRepository.ListLinkedTicketIdsByPhoneNumberAsync(trimmed, cancellationToken);

        var tickets = linkedTicketIds.Count == 0
            ? []
            : (await ticketRepository.SearchCustomerHistoryAsync(
                new CustomerHistoryQuery(
                    VisibleDepartmentIds: null,
                    CrmBuyerCustomerId: null,
                    TicketIds: linkedTicketIds,
                    ExcludeTicketId: null,
                    Limit: TicketLimit,
                    OrderActiveFirst: true),
                cancellationToken)).Tickets;

        return new GenesysCustomerLookupResultDto(
            trimmed,
            found,
            search.CrmStatus,
            search.CrmBuyers,
            search.ExternalSources,
            tickets.Select(ToDto).ToList(),
            tickets.Count(IsOpen));
    }

    /// <summary>A ticket nobody has finished with — what an agent picking up the phone actually needs to spot.</summary>
    private static bool IsOpen(Ticket ticket) =>
        ticket.TicketStatus is not (TicketStatus.Resolved or TicketStatus.Closed);

    private static GenesysCustomerTicketDto ToDto(Ticket ticket) => new(
        ticket.TicketId,
        ticket.TicketNumber,
        ticket.TicketStatus.ToString(),
        IsOpen(ticket),
        ticket.RequestSummary,
        ticket.CurrentDepartmentId,
        ticket.CreatedAtUtc);
}

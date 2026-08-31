using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Customer → Previous Ticket History. Reads exclusively from the existing
/// Tickets (and, for the unverified fallback, IntakeRecords) tables — never
/// a live CRM call — so history is available even when CRM is offline.
///
/// <para>
/// <b>Two identities, never combined.</b> A CRM-verified customer's identity
/// is <c>Ticket.CrmBuyerCustomerId</c> — a phone number may match more than
/// one CRM customer, so history for a verified customer is always scoped by
/// the exact selected <c>CrmBuyerCustomerId</c>, never widened by phone.
/// Only when a ticket has no <c>CrmBuyerCustomerId</c> at all does this
/// service fall back to the persisted <c>IntakeRecord.PhoneNumber</c>
/// snapshot linked to that ticket — a weaker identity (one phone number can
/// belong to more than one real customer), so that result is always labelled
/// <c>"Unverified"</c> rather than presented the same way as verified
/// history.
/// </para>
///
/// <para>
/// <b>Authorization: identical department visibility to the ticket
/// queue/detail (TicketQueryAppService), never widened.</b> Knowing a CRM
/// customer id or a ticket id never grants access to a ticket the caller
/// could not otherwise see — every query here is scoped by the caller's own
/// visible departments, and the ticket-anchored lookup additionally checks
/// the anchor ticket's own department visibility before running anything.
/// </para>
/// </summary>
public sealed class CustomerHistoryAppService(
    ITicketRepository ticketRepository,
    IIntakeRecordRepository intakeRecordRepository,
    TicketQueryAppService ticketQueryAppService)
{
    /// <summary>The New Ticket preview's default page size — a "reasonable latest-ticket limit", never the customer's unbounded history.</summary>
    public const int DefaultLimit = 5;
    public const int MaxLimit = 50;

    /// <summary>
    /// Verified path: <c>GET /api/customers/crm/{crmCustomerId}/ticket-history</c>.
    /// Used by the New Ticket wizard once the agent has explicitly selected a
    /// CRM Buyer/unit — the identity queried is always the one the agent
    /// selected, never inferred from the raw phone search results.
    /// </summary>
    public async Task<CustomerHistoryDto> GetByCrmCustomerIdAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        int crmBuyerCustomerId,
        long? excludeTicketId = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var visibleDepartmentIds = await ticketQueryAppService.ResolveVisibleDepartmentIdsAsync(
            callerEmployeeId, callerRoles, cancellationToken);

        var result = await ticketRepository.SearchCustomerHistoryAsync(
            new CustomerHistoryQuery(visibleDepartmentIds, crmBuyerCustomerId, TicketIds: null, excludeTicketId, NormalizeLimit(limit)),
            cancellationToken);

        return ToDto("Verified", crmBuyerCustomerId, phoneNumberSnapshot: null, result);
    }

    /// <summary>
    /// Ticket-anchored path: <c>GET /api/tickets/{ticketId}/customer-history</c>.
    /// The only entry point Ticket Details uses — it derives the customer
    /// identity from the ticket itself (CrmBuyerCustomerId when present,
    /// otherwise the linked IntakeRecord's phone snapshot), so callers never
    /// supply a raw phone number or CRM id directly. The current ticket is
    /// always excluded from its own history.
    /// </summary>
    public async Task<CustomerHistoryResult> GetForTicketAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return CustomerHistoryResult.Failure(CustomerHistoryOutcome.NotFound);
        }

        if (!await ticketQueryAppService.CanViewDepartmentAsync(callerEmployeeId, callerRoles, ticket.CurrentDepartmentId, cancellationToken))
        {
            return CustomerHistoryResult.Failure(CustomerHistoryOutcome.Forbidden);
        }

        var visibleDepartmentIds = await ticketQueryAppService.ResolveVisibleDepartmentIdsAsync(
            callerEmployeeId, callerRoles, cancellationToken);
        var boundedLimit = NormalizeLimit(limit);

        if (ticket.CrmBuyerCustomerId is { } crmBuyerCustomerId)
        {
            var verifiedResult = await ticketRepository.SearchCustomerHistoryAsync(
                new CustomerHistoryQuery(visibleDepartmentIds, crmBuyerCustomerId, TicketIds: null, ticketId, boundedLimit),
                cancellationToken);

            return CustomerHistoryResult.Success(
                ToDto("Verified", crmBuyerCustomerId, phoneNumberSnapshot: null, verifiedResult, ticket.CrmBuyerCustomerName));
        }

        // Fallback path — no CrmBuyerCustomerId on this ticket at all. The
        // phone number is read from the IntakeRecord this ticket was
        // promoted from (never a live CRM call, never caller-supplied), and
        // ListLinkedTicketIdsByPhoneNumberAsync resolves the matching ticket
        // ids without loading every Ticket into memory to filter by hand.
        var intakeRecord = await intakeRecordRepository.GetByLinkedTicketIdAsync(ticketId, cancellationToken);
        if (intakeRecord is null)
        {
            return CustomerHistoryResult.Success(ToDto("Unverified", null, null, new CustomerHistoryQueryResult([], 0, 0, 0)));
        }

        var linkedTicketIds = await intakeRecordRepository.ListLinkedTicketIdsByPhoneNumberAsync(intakeRecord.PhoneNumber, cancellationToken);
        var unverifiedResult = await ticketRepository.SearchCustomerHistoryAsync(
            new CustomerHistoryQuery(visibleDepartmentIds, CrmBuyerCustomerId: null, linkedTicketIds, ticketId, boundedLimit),
            cancellationToken);

        return CustomerHistoryResult.Success(ToDto("Unverified", null, intakeRecord.PhoneNumber, unverifiedResult));
    }

    private static int NormalizeLimit(int? limit) => limit is null or <= 0 || limit > MaxLimit ? DefaultLimit : limit.Value;

    private static CustomerHistoryDto ToDto(
        string verificationType,
        int? crmBuyerCustomerId,
        string? phoneNumberSnapshot,
        CustomerHistoryQueryResult result,
        string? customerDisplayName = null) =>
        new(
            verificationType,
            crmBuyerCustomerId,
            phoneNumberSnapshot,
            customerDisplayName ?? result.Tickets.Select(t => t.CrmBuyerCustomerName).FirstOrDefault(name => name is not null),
            result.TotalCount,
            result.OpenCount,
            result.ClosedCount,
            result.Tickets.Select(ToTicketDto).ToList());

    private static CustomerHistoryTicketDto ToTicketDto(Ticket ticket) => new(
        ticket.TicketId,
        ticket.TicketNumber,
        ticket.CreatedAtUtc,
        ticket.TicketStatus.ToString(),
        ticket.PriorityId,
        ticket.CategoryId,
        ticket.CurrentDepartmentId,
        ticket.CrmBuyerProjectName ?? ticket.ManualProjectName,
        ticket.CrmBuyerUnitNumber ?? ticket.ManualUnitNumber,
        ticket.VerificationStatus.ToString());
}

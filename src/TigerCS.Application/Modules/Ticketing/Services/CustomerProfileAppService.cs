using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Customer Details/Profile — the Overview/Contact Info/Units tabs on the
/// Customer Profile page. Ticket-anchored, exactly like
/// <see cref="CustomerHistoryAppService.GetForTicketAsync"/>: the identity
/// (<c>CrmBuyerCustomerId</c>) and the department-visibility check both come
/// from the anchor ticket, never from a caller-supplied phone number or CRM
/// id — a caller cannot use this to browse a customer they could not
/// otherwise see via Ticket Details.
///
/// <para>
/// <b>Thin orchestration only — no CRM logic duplicated.</b> This service
/// does exactly three things: authorize against the ticket (reusing
/// <see cref="TicketQueryAppService"/>, same as Ticket Details itself),
/// resolve the phone number from the ticket's own linked IntakeRecord (same
/// resolution <see cref="CustomerHistoryAppService"/> already uses for its
/// unverified fallback), and delegate the actual CRM search to
/// <see cref="CrmBuyerLookupAppService"/> — the same service the New Ticket
/// wizard's CRM Buyer Lookup step uses. It re-filters nothing and re-queries
/// CRM through nothing else.
/// </para>
///
/// <para>
/// <b>Live CRM data, unlike Customer History.</b> Previous Tickets on the
/// Customer Profile page reuses <c>CustomerHistoryAppService</c> unchanged
/// (via its own existing endpoint) precisely because history must work with
/// CRM offline; Overview/Contact Info/Units cannot make that promise — full
/// name (Arabic), mobile number, email, and the customer's full current unit
/// list simply do not exist anywhere in Ticketing's own persisted data, only
/// in CRM. When CRM cannot be searched (unavailable, no longer finds a
/// match, or a data-integrity conflict), <see cref="CustomerProfileDto.Status"/>
/// says so and the caller still knows the ticket's own CrmBuyerCustomerId.
/// </para>
/// </summary>
public sealed class CustomerProfileAppService(
    ITicketRepository ticketRepository,
    IIntakeRecordRepository intakeRecordRepository,
    CrmBuyerLookupAppService crmBuyerLookupAppService,
    TicketQueryAppService ticketQueryAppService)
{
    public async Task<CustomerProfileResult> GetForTicketAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return CustomerProfileResult.Failure(CustomerProfileOutcome.NotFound);
        }

        if (!await ticketQueryAppService.CanViewDepartmentAsync(callerEmployeeId, callerRoles, ticket.CurrentDepartmentId, cancellationToken))
        {
            return CustomerProfileResult.Failure(CustomerProfileOutcome.Forbidden);
        }

        if (ticket.CrmBuyerCustomerId is not { } crmBuyerCustomerId)
        {
            return CustomerProfileResult.Success(Empty(null, "NotCrmVerified"));
        }

        var intakeRecord = await intakeRecordRepository.GetByLinkedTicketIdAsync(ticketId, cancellationToken);
        if (intakeRecord is null)
        {
            return CustomerProfileResult.Success(Empty(crmBuyerCustomerId, "CrmUnavailable"));
        }

        var lookup = await crmBuyerLookupAppService.GetBuyerByPhoneAsync(intakeRecord.PhoneNumber, cancellationToken);
        return CustomerProfileResult.Success(ToDto(crmBuyerCustomerId, lookup));
    }

    private static CustomerProfileDto ToDto(int crmBuyerCustomerId, CrmBuyerLookupResult lookup)
    {
        if (lookup.Outcome == CrmBuyerLookupOutcome.Success && lookup.Buyers is { Count: > 0 } buyers)
        {
            var customer = buyers[0].Customer;
            var units = buyers[0].Units.Select(u => new CustomerProfileUnitDto(
                u.UnitId, u.ProjectName, u.UnitNumber, u.LeadStatus, u.LeadStatusName, u.UnitType, u.FloorNumber)).ToList();
            return new CustomerProfileDto(
                crmBuyerCustomerId, "Found", customer.FullNameEnglish, customer.FullNameArabic, customer.MobileNumber, customer.Email, units);
        }

        var status = lookup.Outcome switch
        {
            CrmBuyerLookupOutcome.AmbiguousCustomerMatch => "AmbiguousCustomerMatch",
            CrmBuyerLookupOutcome.NotFound => "NotFoundInCrm",
            _ => "CrmUnavailable"
        };
        return Empty(crmBuyerCustomerId, status);
    }

    private static CustomerProfileDto Empty(int? crmBuyerCustomerId, string status) =>
        new(crmBuyerCustomerId, status, null, null, null, null, []);
}

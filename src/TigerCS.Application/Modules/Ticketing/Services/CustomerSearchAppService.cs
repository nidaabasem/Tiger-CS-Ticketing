using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The Dashboard/Customer Workspace's standalone customer search: one phone
/// number, searched across the sources the system already integrates,
/// before any intake record or ticket exists. Pure composition — the CRM
/// identity comes from the existing real CRM Buyer Lookup
/// (<see cref="CrmBuyerLookupAppService"/>, the same service the New Ticket
/// wizard calls), and the PACT/Tasleeh legs are
/// <see cref="CustomerLookupAppService.SearchExternalSourcesByPhoneAsync"/> —
/// no gateway, mapping, or verification rule is duplicated here, so the
/// workspace can never disagree with the wizard about who a customer is.
///
/// <para>
/// Read-only and side-effect free: no intake record is created, nothing is
/// persisted, and — matching every existing lookup — no outcome here ever
/// gates ticket creation. A source that fails reports Failed alongside the
/// others' results, never hiding them.
/// </para>
/// </summary>
public sealed class CustomerSearchAppService(
    CrmBuyerLookupAppService crmBuyerLookupAppService,
    CustomerLookupAppService customerLookupAppService)
{
    public async Task<CustomerSearchResultDto> SearchByPhoneAsync(
        string phoneNumber, CancellationToken cancellationToken = default)
    {
        var crmTask = crmBuyerLookupAppService.GetBuyerByPhoneAsync(phoneNumber, cancellationToken);
        var externalTask = customerLookupAppService.SearchExternalSourcesByPhoneAsync(phoneNumber, cancellationToken);

        var crmResult = await crmTask;
        var externalResults = await externalTask;

        var crmStatus = crmResult.Outcome switch
        {
            CrmBuyerLookupOutcome.Success => "Found",
            CrmBuyerLookupOutcome.NotFound => "NotFound",
            CrmBuyerLookupOutcome.AmbiguousCustomerMatch => "AmbiguousMatch",
            _ => "Failed"
        };

        return new CustomerSearchResultDto(
            phoneNumber,
            crmStatus,
            crmResult.Outcome == CrmBuyerLookupOutcome.Success ? crmResult.Buyers ?? [] : [],
            externalResults);
    }
}

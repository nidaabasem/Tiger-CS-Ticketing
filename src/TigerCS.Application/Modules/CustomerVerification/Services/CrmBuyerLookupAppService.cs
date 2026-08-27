using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;

namespace TigerCS.Application.Modules.CustomerVerification.Services;

/// <summary>
/// The only caller of <see cref="ICrmBuyerLookupGateway"/> — searches Tiger
/// CRM by phone number for Buyer records. CRM's own endpoint already filters
/// to units whose Lead is Sold (8) or Contract (9) and whose customer type is
/// Buyer (1), but this service re-applies the same two checks itself rather
/// than trusting CRM's filtering unconditionally (<see cref="IsValidBuyerUnit"/>):
/// Ticketing owns the decision of what counts as a valid Buyer match, CRM is
/// only a read-only data source for it.
///
/// <para>
/// <b>Never resolves ambiguity.</b> A phone number may match multiple CRM
/// customers, and a customer may own multiple valid units — this service
/// returns every one of them, unfiltered beyond the Sold/Contract/Buyer
/// check above, and never guesses which the caller meant. A customer left
/// with zero valid units after filtering is dropped entirely (never
/// returned with an empty <c>Units</c> list); a lookup left with zero
/// customers after that becomes <see cref="CrmBuyerLookupOutcome.NotFound"/>,
/// even though CRM itself answered <see cref="CrmBuyerLookupOutcome.Success"/>.
/// </para>
///
/// <para>
/// No local reference/cache table exists for Buyer matches (see
/// <see cref="ICrmBuyerLookupGateway"/>'s remarks) — this phase does not copy
/// CRM master data into Ticketing; a future ticket-creation flow stores only
/// the CRM identifiers plus an immutable ticket-time snapshot, not a
/// synchronized copy of CRM's own customer/unit tables.
/// </para>
/// </summary>
public sealed class CrmBuyerLookupAppService(ICrmBuyerLookupGateway gateway)
{
    /// <summary>Sold (8) and Contract (9) — the only Lead statuses a Buyer match may carry.</summary>
    private static readonly IReadOnlyCollection<int> ValidLeadStatuses = [1,2,3,4,5,8, 9];

    /// <summary>Buyer — this phase supports Buyer only; any other CRM customer type is never a valid match here.</summary>
    private const int BuyerCustomerType = 1;

    public async Task<CrmBuyerLookupResult> GetBuyerByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default)
    {
        var result = await gateway.GetBuyerByPhoneAsync(phoneNumber, cancellationToken);
        if (result.Outcome != CrmBuyerLookupOutcome.Success || result.Buyers is null)
        {
            return result;
        }

        var validBuyers = result.Buyers
            .Select(buyer => buyer with { Units = buyer.Units.Where(IsValidBuyerUnit).ToList() })
            .Where(buyer => buyer.Units.Count > 0)
            .ToList();

        return validBuyers.Count > 0
            ? CrmBuyerLookupResult.Success(validBuyers, result.Message)
            : CrmBuyerLookupResult.NotFound(result.Message);
    }

    private static bool IsValidBuyerUnit(CrmBuyerUnitDto unit) =>
        ValidLeadStatuses.Contains(unit.LeadStatus) && unit.CustomerType == BuyerCustomerType;
}

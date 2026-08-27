using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;

namespace TigerCS.Application.Modules.CustomerVerification.Services;

/// <summary>
/// The only caller of <see cref="ICrmBuyerLookupGateway"/> — searches Tiger
/// CRM by phone number for Buyer records. CRM's own endpoint is the source of
/// truth for which units are Sold/Contract-eligible: real CRM Lead status
/// codes are not a small, stable, closed set Ticketing can safely hard-code
/// (e.g. status 4 = "Contract" in production, not just 8/9 as an earlier,
/// pre-launch assumption had it), so this service does not re-filter by
/// <see cref="CrmBuyerUnitDto.LeadStatus"/> at all — whatever CRM returns as a
/// match, Ticketing accepts. The one check retained is
/// <see cref="IsValidBuyerUnit"/>'s <c>CustomerType == Buyer</c>: this phase
/// supports Buyer matches only, and that is a Ticketing-side scoping
/// decision, not a guess at CRM's own status semantics.
///
/// <para>
/// <b>Never resolves ambiguity.</b> A phone number may match multiple CRM
/// customers, and a customer may own multiple valid units — this service
/// returns every one of them, unfiltered beyond the Buyer check above, and
/// never guesses which the caller meant. A customer left with zero valid
/// units after filtering is dropped entirely (never returned with an empty
/// <c>Units</c> list); a lookup left with zero customers after that becomes
/// <see cref="CrmBuyerLookupOutcome.NotFound"/>, even though CRM itself
/// answered <see cref="CrmBuyerLookupOutcome.Success"/>.
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

    private static bool IsValidBuyerUnit(CrmBuyerUnitDto unit) => unit.CustomerType == BuyerCustomerType;
}

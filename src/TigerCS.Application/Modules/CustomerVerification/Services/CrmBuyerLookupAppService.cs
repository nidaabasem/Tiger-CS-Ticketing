using Microsoft.Extensions.Logging;
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
/// <b>Business rule: a CRM phone number belongs to exactly one customer.</b>
/// CRM guarantees phone-number uniqueness per customer, so this service
/// always resolves to at most one <see cref="CrmBuyerMatchDto"/> — never a
/// list of distinct customers for the caller to disambiguate. A customer may
/// still own multiple valid units, and every one of them is returned,
/// unfiltered beyond the Buyer check above. A customer left with zero valid
/// units after filtering is dropped entirely; a lookup left with zero
/// customers after that becomes <see cref="CrmBuyerLookupOutcome.NotFound"/>,
/// even though CRM itself answered <see cref="CrmBuyerLookupOutcome.Success"/>.
/// </para>
///
/// <para>
/// <b>Never guesses when CRM breaks its own uniqueness guarantee.</b> CRM
/// naming the same CustomerId more than once is not a distinct-customer
/// situation — <see cref="MergeUnitsForOneCustomer"/> merges those entries'
/// units (deduplicated by UnitId) into one <see cref="CrmBuyerMatchDto"/>.
/// But CRM naming two or more genuinely different CustomerIds for the same
/// phone number is a real data-integrity conflict this service refuses to
/// paper over: it does not pick a "first" customer, does not return a
/// <see cref="CrmBuyerLookupOutcome.Success"/> result at all, and never lets
/// a Ticket or IntakeRecord get linked to either candidate automatically.
/// Instead it returns <see cref="CrmBuyerLookupOutcome.AmbiguousCustomerMatch"/>
/// and logs one warning for investigation — the phone number masked and the
/// distinct CustomerIds named, but never CRM's secret key (this service
/// never has it in the first place; see <c>CrmBuyerHttpGateway</c>). This
/// service never throws for this case either.
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
public sealed class CrmBuyerLookupAppService(ICrmBuyerLookupGateway gateway, ILogger<CrmBuyerLookupAppService> logger)
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

        if (validBuyers.Count == 0)
        {
            return CrmBuyerLookupResult.NotFound(result.Message);
        }

        var distinctCustomerIds = validBuyers.Select(b => b.Customer.CustomerId).Distinct().ToList();
        if (distinctCustomerIds.Count > 1)
        {
            // Data-integrity conflict, not something to guess through — see
            // this type's own remarks. No customer/unit is selected, and no
            // Success result is returned, for any of the candidates.
            logger.LogWarning(
                "CRM GetBuyerByPhone returned {CustomerCount} distinct customers ({CustomerIds}) for {MaskedPhoneNumber}, " +
                "but a CRM phone number is expected to belong to exactly one customer. Treating this as a CRM " +
                "data-integrity conflict — no customer will be auto-selected.",
                distinctCustomerIds.Count, string.Join(",", distinctCustomerIds), Mask(phoneNumber));
            return CrmBuyerLookupResult.AmbiguousCustomerMatch(result.Message);
        }

        var customer = MergeUnitsForOneCustomer(validBuyers);
        return CrmBuyerLookupResult.Success([customer], result.Message);
    }

    /// <summary>
    /// Every entry in <paramref name="entriesForOneCustomer"/> already shares
    /// the same CustomerId (the caller only reaches this once ambiguity
    /// across different customers has been ruled out) — merges their units
    /// into one <see cref="CrmBuyerMatchDto"/>, deduplicated by UnitId, since
    /// CRM fragmenting one customer's Leads across multiple entries is not a
    /// distinct-customer situation.
    /// </summary>
    private static CrmBuyerMatchDto MergeUnitsForOneCustomer(List<CrmBuyerMatchDto> entriesForOneCustomer)
    {
        var mergedUnits = entriesForOneCustomer
            .SelectMany(b => b.Units)
            .GroupBy(u => u.UnitId)
            .Select(g => g.First())
            .ToList();

        return entriesForOneCustomer[0] with { Units = mergedUnits };
    }

    private static bool IsValidBuyerUnit(CrmBuyerUnitDto unit) => unit.CustomerType == BuyerCustomerType;

    /// <summary>Security-Architecture.md §11's masking discipline, applied for the one diagnostic log line above — enough to correlate, never enough to identify.</summary>
    private static string Mask(string phoneNumber) =>
        phoneNumber.Length <= 4 ? "***" : $"***{phoneNumber[^4..]}";
}

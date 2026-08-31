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
/// <b>Resilient, not naive, about CRM breaking its own uniqueness
/// guarantee.</b> <see cref="ConsolidateToSingleCustomer"/> handles a CRM
/// response that names the same CustomerId more than once (its units are
/// merged, deduplicated by UnitId — CRM fragmenting one customer's Leads
/// across multiple entries is not a distinct-customer situation) and, for the
/// genuinely unexpected case of two different CustomerIds answering the same
/// phone number, deterministically keeps the first one CRM returned and logs
/// a warning for investigation — this service never throws, and never hands
/// a caller built for "one customer" an ambiguous multi-customer result.
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

        var customer = ConsolidateToSingleCustomer(validBuyers, phoneNumber);
        return CrmBuyerLookupResult.Success([customer], result.Message);
    }

    /// <summary>See this type's own remarks for the business rule and the resilience behavior below.</summary>
    private CrmBuyerMatchDto ConsolidateToSingleCustomer(List<CrmBuyerMatchDto> validBuyers, string phoneNumber)
    {
        var distinctCustomerIds = validBuyers.Select(b => b.Customer.CustomerId).Distinct().ToList();
        var primaryCustomerId = distinctCustomerIds[0];

        if (distinctCustomerIds.Count > 1)
        {
            logger.LogWarning(
                "CRM GetBuyerByPhone returned {CustomerCount} distinct customers for {MaskedPhoneNumber}, " +
                "but a CRM phone number is expected to belong to exactly one customer. Using the first customer " +
                "returned (CustomerId {PrimaryCustomerId}) and discarding the rest.",
                distinctCustomerIds.Count, Mask(phoneNumber), primaryCustomerId);
        }

        var entriesForPrimaryCustomer = validBuyers.Where(b => b.Customer.CustomerId == primaryCustomerId).ToList();
        var mergedUnits = entriesForPrimaryCustomer
            .SelectMany(b => b.Units)
            .GroupBy(u => u.UnitId)
            .Select(g => g.First())
            .ToList();

        return entriesForPrimaryCustomer[0] with { Units = mergedUnits };
    }

    private static bool IsValidBuyerUnit(CrmBuyerUnitDto unit) => unit.CustomerType == BuyerCustomerType;

    /// <summary>Security-Architecture.md §11's masking discipline, applied for the one diagnostic log line above — enough to correlate, never enough to identify.</summary>
    private static string Mask(string phoneNumber) =>
        phoneNumber.Length <= 4 ? "***" : $"***{phoneNumber[^4..]}";
}

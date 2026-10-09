using System.Globalization;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

public enum VerifiedBuyerOutcome
{
    Resolved,
    CustomerReferenceInvalid,
    PhoneNumberInvalid,
    UnitIdInvalid,
    CustomerNotVerified,
    UnitNotEligible,
    AmbiguousCustomer,
    CrmUnavailable
}

/// <param name="Outcome">Why resolution succeeded or failed.</param>
/// <param name="CustomerId">The CRM customer id (valid when <see cref="VerifiedBuyerOutcome.Resolved"/>).</param>
/// <param name="Buyer">CRM's single buyer for the number, with the eligible units.</param>
/// <param name="Unit">The requested unit when a unit id was supplied; null when none was.</param>
public sealed record VerifiedBuyerResult(
    VerifiedBuyerOutcome Outcome, int CustomerId = 0, CrmBuyerMatchDto? Buyer = null, CrmBuyerUnitDto? Unit = null);

/// <summary>
/// <b>The one customer/unit ownership rule</b> shared by the unit-details API
/// and the OTP flow: the named customer must be exactly the customer CRM
/// resolves for the number (through <see cref="CrmBuyerLookupAppService"/>, the
/// same lookup the New Ticket wizard uses) and, when a unit is named, that unit
/// must be one of that customer's own Buyer units. Neither the customer id nor
/// the unit id is trusted on its own, and a unit that is someone else's gets the
/// same answer as one that does not exist.
/// </summary>
public sealed class GenesysVerifiedBuyerResolver(
    CrmBuyerLookupAppService crmBuyerLookupAppService,
    ILogger<GenesysVerifiedBuyerResolver> logger)
{
    public async Task<VerifiedBuyerResult> ResolveAsync(
        string? customerReference, string? phoneNumber, int? unitId, CancellationToken cancellationToken = default)
    {
        if (!TryParseCustomerId(customerReference, out var customerId))
        {
            return new(VerifiedBuyerOutcome.CustomerReferenceInvalid);
        }

        var searched = CustomerPhoneNumber.FromTelephonyAddress(phoneNumber);
        if (searched is null)
        {
            return new(VerifiedBuyerOutcome.PhoneNumberInvalid);
        }

        if (unitId is <= 0)
        {
            return new(VerifiedBuyerOutcome.UnitIdInvalid);
        }

        var lookup = await crmBuyerLookupAppService.GetBuyerByPhoneAsync(searched, cancellationToken);
        switch (lookup.Outcome)
        {
            case CrmBuyerLookupOutcome.Success:
                break;
            case CrmBuyerLookupOutcome.NotFound:
                return new(VerifiedBuyerOutcome.CustomerNotVerified);
            case CrmBuyerLookupOutcome.AmbiguousCustomerMatch:
                return new(VerifiedBuyerOutcome.AmbiguousCustomer);
            default:
                return new(VerifiedBuyerOutcome.CrmUnavailable);
        }

        // At most one customer per phone number (CrmBuyerLookupAppService).
        var buyer = lookup.Buyers?.SingleOrDefault();
        if (buyer is null || buyer.Customer.CustomerId != customerId)
        {
            logger.LogWarning(
                "Request named customer {RequestedCustomerId}, which is not the customer CRM resolved for the verified number.",
                customerId);
            return new(VerifiedBuyerOutcome.CustomerNotVerified);
        }

        if (unitId is null)
        {
            return new(VerifiedBuyerOutcome.Resolved, customerId, buyer);
        }

        var unit = buyer.Units.FirstOrDefault(u => u.UnitId == unitId);
        if (unit is null)
        {
            logger.LogWarning(
                "Customer {CustomerId} asked for unit {UnitId}, which is not among their eligible units.", customerId, unitId);
            return new(VerifiedBuyerOutcome.UnitNotEligible);
        }

        return new(VerifiedBuyerOutcome.Resolved, customerId, buyer, unit);
    }

    /// <summary>The Genesys lookup returns the CRM customer id as plain text (<c>externalCustomerId</c>); the Customers directory key is <c>crm:{id}</c>. Both are accepted.</summary>
    internal static bool TryParseCustomerId(string? reference, out int customerId)
    {
        customerId = 0;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        if (CustomerIdentity.TryParse(reference, out var identity))
        {
            if (identity.Kind != CustomerIdentityKind.Crm)
            {
                return false;
            }

            customerId = identity.CrmBuyerCustomerId!.Value;
            return true;
        }

        return int.TryParse(reference.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out customerId) && customerId > 0;
    }
}

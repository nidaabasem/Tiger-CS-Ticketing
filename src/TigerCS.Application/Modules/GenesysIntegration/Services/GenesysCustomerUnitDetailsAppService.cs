using System.Globalization;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.CrmDocuments;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

public enum GenesysUnitDetailsOutcome
{
    UnitDetails,
    UnitSelectionRequired,
    IntegrationDisabled,
    CustomerReferenceInvalid,
    PhoneNumberInvalid,
    UnitIdInvalid,
    CustomerNotVerified,
    UnitNotEligible,
    AmbiguousCustomer,
    CrmUnavailable
}

public sealed record GenesysUnitDetailsResult(GenesysUnitDetailsOutcome Outcome, GenesysCustomerUnitDetailsResponse? Response = null);

/// <summary>
/// <c>POST /api/genesys/customers/unit-details</c>: the unit and project facts
/// a chatbot/voicebot may tell a verified customer.
///
/// <para>
/// <b>Authorization is decided here, against CRM, on every call.</b> The
/// request names a customer and (optionally) a unit, but neither is trusted.
/// The verified phone number is looked up through
/// <see cref="GenesysVerifiedBuyerResolver"/> — the same service the New Ticket
/// wizard and the Genesys lookup use — and the request is served only when
/// (1) that lookup resolves to exactly the customer the request names, and
/// (2) the requested unit is one of that customer's own Buyer units
/// (Sold/Contract, as CRM itself filters). A unit id alone grants nothing; a
/// unit that exists but belongs to someone else is refused with the same
/// answer as one that does not exist, so ids cannot be probed.
/// </para>
///
/// <para>
/// <b>What is returned.</b> Unit and project fields CRM's buyer lookup
/// provides are mapped as-is. Facts that lookup does not carry (tower,
/// bedrooms, area, parking, project address/status/description/amenities,
/// handover dates) come from <see cref="ICrmUnitDetailsGateway"/> and are null
/// whenever that source has nothing — never defaulted or derived. Unit-level
/// and project-level handover dates are returned in separate fields.
/// Nothing from CRM beyond the mapped fields (no internal notes, no other
/// customer data, no contact details) is copied into the response.
/// </para>
///
/// <para>
/// <b>Private financial data needs proof, not just ownership.</b> The
/// customer's actual sold price and registration cost are returned only when
/// the request carries a <c>verificationSessionId</c> produced by the server's own
/// one-time-code verification (email or SMS) for this very customer, unit and Lead,
/// owned by the calling service account and unexpired — the same evidence document
/// copies require. A <c>verificationMethod</c> a caller merely asserted is never proof,
/// and neither is a phone number or customer id the caller typed.
/// Without proof CRM is not even asked for the sale. With it, the sale is
/// requested for the Lead the buyer lookup bound to this unit and discarded
/// unless CRM echoes that same Lead, so another buyer's sale or an unrelated
/// historical booking cannot be shown.
/// </para>
/// </summary>
public sealed class GenesysCustomerUnitDetailsAppService(
    GenesysOptions options,
    GenesysVerifiedBuyerResolver buyerResolver,
    ICrmUnitDetailsGateway unitDetailsGateway,
    CrmDocumentOptions verificationPolicy,
    IVerificationSessionRepository sessionRepository,
    IUnitReferenceRepository unitReferenceRepository,
    ICustomerOtpChallengeRepository otpChallengeRepository,
    TimeProvider timeProvider,
    ILogger<GenesysCustomerUnitDetailsAppService> logger)
{
    public async Task<GenesysUnitDetailsResult> GetAsync(
        string? customerReference, string? phoneNumber, int? unitId,
        Guid? callerEmployeeId = null, Guid? verificationSessionId = null, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return new(GenesysUnitDetailsOutcome.IntegrationDisabled);
        }

        var resolved = await buyerResolver.ResolveAsync(customerReference, phoneNumber, unitId, cancellationToken);
        switch (resolved.Outcome)
        {
            case VerifiedBuyerOutcome.Resolved:
                break;
            case VerifiedBuyerOutcome.CustomerReferenceInvalid:
                return new(GenesysUnitDetailsOutcome.CustomerReferenceInvalid);
            case VerifiedBuyerOutcome.PhoneNumberInvalid:
                return new(GenesysUnitDetailsOutcome.PhoneNumberInvalid);
            case VerifiedBuyerOutcome.UnitIdInvalid:
                return new(GenesysUnitDetailsOutcome.UnitIdInvalid);
            case VerifiedBuyerOutcome.CustomerNotVerified:
                return new(GenesysUnitDetailsOutcome.CustomerNotVerified);
            case VerifiedBuyerOutcome.UnitNotEligible:
                return new(GenesysUnitDetailsOutcome.UnitNotEligible);
            case VerifiedBuyerOutcome.AmbiguousCustomer:
                return new(GenesysUnitDetailsOutcome.AmbiguousCustomer);
            default:
                return new(GenesysUnitDetailsOutcome.CrmUnavailable);
        }

        var buyer = resolved.Buyer!;
        var customerId = resolved.CustomerId;
        var customerKey = CustomerIdentity.Crm(customerId).Key;

        if (unitId is null)
        {
            var eligible = buyer.Units
                .Select(u => new GenesysEligibleUnitDto(u.UnitId, u.UnitNumber, u.ProjectId, u.ProjectName, u.FloorNumber, u.LeadStatusName))
                .ToList();
            return new(
                GenesysUnitDetailsOutcome.UnitSelectionRequired,
                new GenesysCustomerUnitDetailsResponse("UnitSelectionRequired", customerKey, eligible, null, null, null, null));
        }

        var unit = resolved.Unit!;

        var proof = await CheckProofAsync(callerEmployeeId, verificationSessionId, customerId, unit, cancellationToken);

        CrmUnitDetailsResult enrichment;
        try
        {
            enrichment = await unitDetailsGateway.GetUnitDetailsAsync(
                customerId, unit.UnitId, unit.LeadId, includeSale: proof == ProofState.Valid, cancellationToken);
        }
        catch (CrmGatewayUnavailableException ex)
        {
            logger.LogWarning(ex, "CRM unit-details source unavailable for unit {UnitId}.", unit.UnitId);
            enrichment = CrmUnitDetailsResult.Unavailable();
        }

        return new(
            GenesysUnitDetailsOutcome.UnitDetails,
            Map(customerKey, unit, enrichment, proof));
    }

    private enum ProofState { Missing, Invalid, Valid }

    /// <summary>
    /// The sale is private, so it needs <b>server-recorded</b> evidence, never an assertion. A session qualifies only when
    /// <i>all</i> of these hold: it is owned by the calling account, confirmed and unexpired; it carries the proof the OTP service
    /// attaches (<c>ProofChallengeId</c>, <c>CrmBuyerCustomerId</c>, <c>CrmBuyerLeadId</c>) — which no agent-asserted session has,
    /// whatever <c>verificationMethod</c> it names; the proof was bound to <i>this</i> CRM customer and <i>this</i> unit's Lead; the
    /// session's unit is this unit; and the recorded challenge itself exists, was spent by a correct code, produced this very
    /// session for this caller, and is bound to the same customer, Lead and unit. Unknown, foreign, unconfirmed, expired,
    /// asserted, other-customer, other-unit and other-Lead sessions all give the same answer (<see cref="ProofState.Invalid"/>), so session
    /// ids cannot be probed. A phone number or customer id alone is never proof.
    /// </summary>
    private async Task<ProofState> CheckProofAsync(
        Guid? callerEmployeeId, Guid? verificationSessionId, int customerId, CrmBuyerUnitDto unit, CancellationToken cancellationToken)
    {
        if (verificationSessionId is not { } sessionId || sessionId == Guid.Empty)
        {
            return ProofState.Missing;
        }

        if (callerEmployeeId is not { } caller)
        {
            return ProofState.Invalid;
        }

        var session = await sessionRepository.GetByIdAsync(sessionId, cancellationToken);
        if (session is null
            || !session.IsOwnedBy(caller)
            || session.Status is not (VerificationSessionStatus.Confirmed or VerificationSessionStatus.Consumed)
            || session.ExpiresAtUtc <= timeProvider.GetUtcNow().UtcDateTime
            || !verificationPolicy.IsAccepted(session.VerificationMethod)
            || session.ProofChallengeId is not { } challengeId
            || session.CrmBuyerCustomerId != customerId
            || session.CrmBuyerLeadId != unit.LeadId)
        {
            return ProofState.Invalid;
        }

        var verifiedUnit = await unitReferenceRepository.GetByIdAsync(session.UnitReferenceId, cancellationToken);
        if (verifiedUnit is null
            || !string.Equals(verifiedUnit.CrmUnitId, unit.UnitId.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
        {
            return ProofState.Invalid;
        }

        var challenge = await otpChallengeRepository.GetByIdAsync(challengeId, cancellationToken);
        return challenge is not null
            && challenge.CallerEmployeeId == caller
            && challenge.Status == OtpChallengeStatus.Verified
            && challenge.VerificationSessionId == session.VerificationSessionId
            && challenge.CrmCustomerId == customerId
            && challenge.CrmLeadId == unit.LeadId
            && challenge.UnitReferenceId == session.UnitReferenceId
                ? ProofState.Valid
                : ProofState.Invalid;
    }

    private GenesysCustomerUnitDetailsResponse Map(string customerKey, CrmBuyerUnitDto unit, CrmUnitDetailsResult enrichment, ProofState proof)
    {
        var details = enrichment.Outcome == CrmUnitDetailsOutcome.Found ? enrichment.Details : null;
        var project = details?.Project;

        var unitDto = new GenesysUnitDetailsDto(
            unit.UnitId,
            unit.UnitNumber,
            details?.TowerName,
            unit.FloorNumber,
            new GenesysUnitTypeDto(unit.UnitType, details?.UnitTypeName),
            details?.Bedrooms,
            details?.Area is { } area ? new GenesysAreaDto(area, details.AreaUnit) : null,
            new GenesysBookingDto(unit.LeadId.ToString(CultureInfo.InvariantCulture), unit.LeadStatusName, unit.LeadStatus),
            details?.Parking?.Select(p => new GenesysParkingDto(p.Number, p.Level, p.Type)).ToList(),
            Iso(details?.UnitExpectedHandoverDate),
            Iso(details?.UnitActualHandoverDate));

        var projectDto = new GenesysProjectDetailsDto(
            unit.ProjectId,
            unit.ProjectName,
            unit.ProjectArabicName,
            project?.Address,
            project?.Status,
            Iso(project?.ExpectedHandoverDate),
            Iso(project?.ActualHandoverDate),
            project?.Description,
            project?.Amenities?.ToList(),
            project?.CompletionPercentage,
            Iso(project?.ExpectedCompletionDate),
            Iso(project?.ActualCompletionDate));

        // Which recorded pair applies. A choice between recorded values only.
        var handoverSource =
            unitDto.ExpectedHandoverDate is not null || unitDto.ActualHandoverDate is not null ? "Unit"
            : projectDto.ExpectedHandoverDate is not null || projectDto.ActualHandoverDate is not null ? "Project"
            : null;

        var status = enrichment.Outcome switch
        {
            CrmUnitDetailsOutcome.Found => "Available",
            CrmUnitDetailsOutcome.Unavailable => "Unavailable",
            _ => "NotAvailable"
        };

        var (sale, financialStatus) = MapSale(unit, enrichment, details, proof);

        return new GenesysCustomerUnitDetailsResponse(
            "UnitDetails", customerKey, [], unitDto, projectDto, handoverSource, status, sale, financialStatus);
    }

    /// <summary>The sale is shown only with valid proof and only when CRM read it from the Lead the buyer lookup bound to this unit.</summary>
    private GenesysSaleMapping MapSale(CrmBuyerUnitDto unit, CrmUnitDetailsResult enrichment, CrmUnitDetails? details, ProofState proof)
    {
        switch (proof)
        {
            case ProofState.Missing:
                return new(null, "VerificationRequired");
            case ProofState.Invalid:
                return new(null, "VerificationFailed");
        }

        if (enrichment.Outcome == CrmUnitDetailsOutcome.Unavailable)
        {
            return new(null, "Unavailable");
        }

        var crmSale = details?.Sale;
        if (crmSale is null)
        {
            return new(null, "NotAvailable");
        }

        if (crmSale.LeadId != unit.LeadId)
        {
            logger.LogWarning(
                "CRM returned a sale for lead {ReturnedLeadId}, not the lead {ExpectedLeadId} bound to unit {UnitId}; it was discarded.",
                crmSale.LeadId, unit.LeadId, unit.UnitId);
            return new(null, "NotAvailable");
        }

        return new(
            new GenesysSaleDto(
                crmSale.SoldPrice is { } price ? new GenesysMoneyDto(price, crmSale.Currency) : null,
                crmSale.RegistrationCost is { } fee ? new GenesysMoneyDto(fee, crmSale.Currency) : null),
            "Available");
    }

    private readonly record struct GenesysSaleMapping(GenesysSaleDto? Sale, string Status);

    private static string? Iso(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.CustomerVerification.Dto;

/// <summary>A CRM customer, as returned by <c>GET /TicketingSystem/GetBuyerByPhone</c>.</summary>
/// <param name="CustomerId">The customer's identifier in Tiger CRM.</param>
/// <param name="FullNameEnglish">The customer's name in English, when CRM records one.</param>
/// <param name="FullNameArabic">The customer's name in Arabic, when CRM records one.</param>
/// <param name="MobileNumber">The customer's mobile number on file in CRM.</param>
/// <param name="Email">The customer's email, when CRM records one.</param>
public sealed record CrmCustomerDto(
    int CustomerId, string? FullNameEnglish, string? FullNameArabic, string? MobileNumber, string? Email);

/// <summary>
/// One unit a CRM buyer owns, carrying the Lead it was sold/contracted
/// through and the project it belongs to. CRM's own <c>GetBuyerByPhone</c>
/// endpoint already determines which units are Sold/Contract-eligible — real
/// CRM Lead status codes are not a small, stable set Ticketing can safely
/// hard-code (production has returned e.g. status 4 = "Contract"), so
/// <c>CrmBuyerLookupAppService</c> does not re-filter by
/// <paramref name="LeadStatus"/>. It does still require
/// <paramref name="CustomerType"/> to be Buyer (1) — this phase's own scoping
/// decision, not a guess at CRM's status semantics.
/// </summary>
/// <param name="LeadId">The CRM Lead identifier this unit was matched through.</param>
/// <param name="LeadStatus">CRM's Lead status code. Not re-validated by Ticketing — CRM's own filtering already determined eligibility.</param>
/// <param name="LeadStatusName">CRM's display name for <paramref name="LeadStatus"/> (e.g. "Sold", "Contract").</param>
/// <param name="UnitId">The unit's identifier in Tiger CRM.</param>
/// <param name="UnitNumber">The unit number.</param>
/// <param name="UnitStatus">CRM's unit status code.</param>
/// <param name="UnitType">CRM's unit type code.</param>
/// <param name="FloorNumber">The unit's floor, when CRM records one.</param>
/// <param name="ProjectId">The project's identifier in Tiger CRM.</param>
/// <param name="ProjectName">The project's name in English.</param>
/// <param name="ProjectArabicName">The project's name in Arabic.</param>
/// <param name="CustomerType">1 = Buyer. This phase supports Buyer only.</param>
/// <param name="CustomerTypeName">CRM's display name for <paramref name="CustomerType"/> (e.g. "Buyer").</param>
/// <param name="ProjectCode">The PACT unit-code prefix CRM holds for the project (<c>TP125</c>), when CRM publishes it (not yet part of the deployed <c>GetBuyerByPhone</c>). When present it is CRM's own statement of the project's PACT code and replaces any guess; null = not published.</param>
/// <remarks>Also exposes the derived <see cref="UnitCode"/> (read-only).</remarks>
public sealed record CrmBuyerUnitDto(
    int LeadId,
    int LeadStatus,
    string? LeadStatusName,
    int UnitId,
    string? UnitNumber,
    int UnitStatus,
    int UnitType,
    int? FloorNumber,
    int ProjectId,
    string? ProjectName,
    string? ProjectArabicName,
    int CustomerType,
    string? CustomerTypeName,
    string? ProjectCode = null)
{
    /// <summary>
    /// The PACT unit code of this unit (<c>TP130-902-B</c>): the CRM project code
    /// and the unit number, suffix preserved — the same key
    /// <c>CollectionsUnitKey.FromCrm</c> builds. Null when
    /// <see cref="ProjectCode"/> is missing or is not a <c>TP</c>/numeric tower code, or
    /// <see cref="UnitNumber"/> is blank: nothing is guessed, and it is never derived
    /// from <see cref="ProjectId"/> (a CRM id is not a PACT tower number). Computed, so
    /// it can never disagree with the fields it comes from; additive on the wire.
    /// </summary>
    public string? UnitCode => CrmUnitCode.From(ProjectCode, UnitNumber);
}

/// <summary>Builds the PACT-style unit code of a CRM unit, refusing any project code that is not a tower code.</summary>
public static class CrmUnitCode
{
    /// <summary><c>projectCode + "-" + unitNumber</c>, normalized (upper case, one <c>TP</c> prefix); null when either part is missing or the project code is not <c>TP</c>+digits (or plain digits).</summary>
    public static string? From(string? projectCode, string? unitNumber)
    {
        var project = projectCode?.Trim();
        if (string.IsNullOrEmpty(project)) return null;
        var digits = project.StartsWith("TP", StringComparison.OrdinalIgnoreCase) ? project[2..] : project;
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) return null;

        var code = CollectionsUnitKey.FromCrm(project, unitNumber);
        return CollectionsUnitKey.IsListable(code) ? code : null;
    }
}

/// <summary>
/// One CRM customer matched by phone, plus every valid Buyer unit they own.
/// Business rule: a CRM phone number belongs to exactly one customer, so
/// <c>CrmBuyerLookupResult.Buyers</c> carries at most one of these
/// (<c>CrmBuyerLookupAppService</c> consolidates to a single customer even if
/// CRM's own response does not honor that). A single customer can still own
/// multiple units, and none of them is ever auto-selected — the agent always
/// picks the relevant one.
/// </summary>
public sealed record CrmBuyerMatchDto(CrmCustomerDto Customer, IReadOnlyList<CrmBuyerUnitDto> Units);

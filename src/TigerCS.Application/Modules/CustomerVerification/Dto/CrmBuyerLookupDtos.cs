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
    string? CustomerTypeName);

/// <summary>
/// One CRM customer matched by phone, plus every valid Buyer unit they own.
/// A single phone number can resolve to multiple <see cref="CrmBuyerMatchDto"/>
/// entries (CRM does not guarantee a phone number is unique), and a single
/// customer can own multiple units — neither is ever collapsed or
/// auto-selected here.
/// </summary>
public sealed record CrmBuyerMatchDto(CrmCustomerDto Customer, IReadOnlyList<CrmBuyerUnitDto> Units);

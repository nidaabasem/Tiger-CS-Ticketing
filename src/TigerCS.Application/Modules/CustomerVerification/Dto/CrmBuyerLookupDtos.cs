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
/// through and the project it belongs to. Only units whose
/// <paramref name="LeadStatus"/> is Sold (8) or Contract (9) and whose
/// <paramref name="CustomerType"/> is Buyer (1) are valid for ticket
/// creation — CRM already filters to this, and <c>CrmBuyerLookupAppService</c>
/// re-checks it rather than trusting CRM's filtering alone.
/// </summary>
/// <param name="LeadId">The CRM Lead identifier this unit was matched through.</param>
/// <param name="LeadStatus">8 = Sold, 9 = Contract. Any other value is not a valid Buyer match.</param>
/// <param name="LeadStatusName">CRM's display name for <paramref name="LeadStatus"/> (e.g. "Sold").</param>
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

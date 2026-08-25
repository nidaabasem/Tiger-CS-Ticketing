namespace TigerCS.Integrations.Modules.CrmIntegration;

/// <summary>
/// The exact wire shape of <c>GET /TicketingSystem/GetBuyerByPhone</c>'s JSON
/// body, deserialized with <c>PropertyNameCaseInsensitive</c> so these
/// PascalCase record properties bind to CRM's camelCase JSON without needing
/// a <c>JsonPropertyName</c> attribute per member. Never referenced outside
/// <see cref="CrmBuyerHttpGateway"/> — everything past the gateway sees only
/// the mapped <c>TigerCS.Application.Modules.CustomerVerification.Dto</c>
/// types.
/// </summary>
internal sealed record CrmBuyerLookupHttpResponse(bool Success, bool Found, string? Message, List<CrmBuyerHttpDto>? Buyers);

internal sealed record CrmBuyerHttpDto(CrmCustomerHttpDto Customer, List<CrmBuyerUnitHttpDto> Units);

internal sealed record CrmCustomerHttpDto(
    int CustomerId, string? FullNameEnglish, string? FullNameArabic, string? MobileNumber, string? Email);

internal sealed record CrmBuyerUnitHttpDto(
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

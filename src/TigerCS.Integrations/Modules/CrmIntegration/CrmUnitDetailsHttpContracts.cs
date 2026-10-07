namespace TigerCS.Integrations.Modules.CrmIntegration;

/// <summary>
/// The <b>proposed</b> wire shape of Tiger CRM's
/// <c>GET /TicketingSystem/GetUnitDetails?customerId=&amp;unitId=</c> — same
/// envelope and <c>X-SECRET-KEY</c> convention as <c>GetBuyerByPhone</c>.
/// It is the contract the CRM side must implement; it is NOT yet verified
/// against a deployed CRM, and the CRM source has not been inspected to bind
/// each member to a column (docs/Genesys/Customer-Unit-Details-API.md §CRM
/// contract). Dates are strings so a legacy MVC serializer's date format never
/// fails the whole answer; <see cref="CrmUnitDetailsHttpGateway"/> parses them
/// and treats an unreadable one as not recorded.
/// </summary>
internal sealed record CrmUnitDetailsHttpResponse(bool Success, bool Found, string? Message, CrmUnitDetailsHttpDto? Unit);

internal sealed record CrmUnitDetailsHttpDto(
    string? UnitTypeName,
    string? TowerName,
    int? Bedrooms,
    decimal? Area,
    string? AreaUnit,
    List<CrmParkingHttpDto>? Parking,
    string? ExpectedHandoverDate,
    string? ActualHandoverDate,
    CrmProjectDetailsHttpDto? Project);

internal sealed record CrmParkingHttpDto(string? Number, string? Level, string? Type);

internal sealed record CrmProjectDetailsHttpDto(
    string? Address,
    string? Status,
    string? ExpectedHandoverDate,
    string? ActualHandoverDate,
    string? Description,
    List<string>? Amenities);

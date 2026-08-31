namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>
/// Customer Details/Profile — ticket-anchored, live CRM data for the
/// Overview/Contact Info/Units tabs (Previous Tickets reuses
/// <c>CustomerHistoryDto</c> unchanged, via the existing
/// <c>GET /api/tickets/{ticketId}/customer-history</c> endpoint — this DTO
/// carries none of that).
///
/// <para>
/// <b>Identity is always the anchor ticket's own CrmBuyerCustomerId, never a
/// live-refreshed value.</b> <see cref="CrmBuyerCustomerId"/> is null only
/// when the ticket itself was never CRM Buyer verified
/// (<see cref="Status"/> <c>"NotCrmVerified"</c>) — there is no customer
/// identity to show a profile for. Every other field comes from a fresh
/// <c>CrmBuyerLookupAppService.GetBuyerByPhoneAsync</c> call (the same
/// service the New Ticket wizard uses — never duplicated), so it reflects
/// CRM's current data, not the ticket-time snapshot
/// <c>Ticket.CrmBuyerCustomerName</c>/etc. already show elsewhere.
/// </para>
/// </summary>
/// <param name="CrmBuyerCustomerId">The anchor ticket's own CrmBuyerCustomerId, or null when the ticket was never CRM Buyer verified.</param>
/// <param name="Status">One of "NotCrmVerified", "Found", "CrmUnavailable", "AmbiguousCustomerMatch", "NotFoundInCrm". Only "Found" populates the fields below.</param>
/// <param name="FullNameEnglish">The customer's name in English, when CRM records one.</param>
/// <param name="FullNameArabic">The customer's name in Arabic, when CRM records one.</param>
/// <param name="MobileNumber">The customer's mobile number on file in CRM.</param>
/// <param name="Email">The customer's email, when CRM records one.</param>
/// <param name="Units">Every eligible (Sold/Contract, Buyer) unit this customer currently owns — not just the anchor ticket's own unit.</param>
public sealed record CustomerProfileDto(
    int? CrmBuyerCustomerId,
    string Status,
    string? FullNameEnglish,
    string? FullNameArabic,
    string? MobileNumber,
    string? Email,
    IReadOnlyList<CustomerProfileUnitDto> Units);

/// <summary>One unit a CRM Buyer customer currently owns, as returned live by CRM's own GetBuyerByPhone — see <c>CrmBuyerUnitDto</c> for field provenance.</summary>
public sealed record CustomerProfileUnitDto(
    int UnitId,
    string? ProjectName,
    string? UnitNumber,
    int LeadStatus,
    string? LeadStatusName,
    int UnitType,
    int? FloorNumber);

public enum CustomerProfileOutcome
{
    Success,
    NotFound,
    Forbidden
}

public sealed record CustomerProfileResult(CustomerProfileOutcome Outcome, CustomerProfileDto? Response = null)
{
    public static CustomerProfileResult Success(CustomerProfileDto response) => new(CustomerProfileOutcome.Success, response);
    public static CustomerProfileResult Failure(CustomerProfileOutcome outcome) => new(outcome);
}

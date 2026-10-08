namespace TigerCS.Application.Modules.GenesysIntegration.Dto;

/// <summary>
/// The customer-facing answer to <c>POST /api/genesys/customers/unit-details</c>.
/// <see cref="Mode"/> is <c>UnitSelectionRequired</c> (no <c>unitId</c> was
/// sent: <see cref="EligibleUnits"/> is filled so the bot can ask which unit)
/// or <c>UnitDetails</c> (<see cref="Unit"/>, <see cref="Project"/> and
/// <see cref="HandoverDateSource"/> are filled, <see cref="EligibleUnits"/> is empty).
/// Unavailable values are null, never guessed.
/// </summary>
/// <param name="Mode">UnitSelectionRequired or UnitDetails.</param>
/// <param name="CustomerReference">The verified customer, as <c>crm:{id}</c>.</param>
/// <param name="EligibleUnits">The customer's eligible units; empty in UnitDetails mode.</param>
/// <param name="Unit">The selected unit.</param>
/// <param name="Project">The selected unit's project.</param>
/// <param name="HandoverDateSource">Which recorded handover pair applies: <c>Unit</c> when CRM recorded any date on the unit, otherwise <c>Project</c> when it recorded one on the project, otherwise null. Selects between recorded values only — no date is derived.</param>
/// <param name="DetailsStatus">UnitDetails mode: <c>Available</c> when the enrichment source answered, <c>NotAvailable</c> when no source publishes those facts, <c>Unavailable</c> when the source could not be reached. Explains why enrichment values are null.</param>
/// <param name="Sale">The customer's own recorded sale (sold price, registration cost). Private financial data: present only when <see cref="FinancialDetailsStatus"/> is <c>Available</c>.</param>
/// <param name="FinancialDetailsStatus">UnitDetails mode: <c>Available</c> (CRM answered for this customer's own sale), <c>VerificationRequired</c> (no <c>verificationSessionId</c> sent), <c>VerificationFailed</c> (unknown, foreign, unconfirmed, expired, weak-method, or for a different unit), <c>NotAvailable</c> (CRM has no sale data for it, or it could not be tied to the customer's own Lead), <c>Unavailable</c> (CRM unreachable). Null in UnitSelectionRequired mode. Independent of <see cref="DetailsStatus"/>.</param>
public sealed record GenesysCustomerUnitDetailsResponse(
    string Mode,
    string CustomerReference,
    IReadOnlyList<GenesysEligibleUnitDto> EligibleUnits,
    GenesysUnitDetailsDto? Unit,
    GenesysProjectDetailsDto? Project,
    string? HandoverDateSource,
    string? DetailsStatus,
    GenesysSaleDto? Sale = null,
    string? FinancialDetailsStatus = null);

/// <summary>An amount and the currency CRM recorded it in. The currency is null when CRM does not say — it is never assumed.</summary>
public sealed record GenesysMoneyDto(decimal Amount, string? Currency);

/// <param name="SoldPrice">The price this customer actually agreed to pay for the unit — not the unit's current list price. Null when not recorded; 0 only when CRM recorded 0.</param>
/// <param name="RegistrationCost">The registration fee amount recorded for this sale (an amount, not a percentage, and not derived from the price). Null when not recorded; 0 only when CRM recorded 0.</param>
public sealed record GenesysSaleDto(GenesysMoneyDto? SoldPrice, GenesysMoneyDto? RegistrationCost);

/// <summary>One selectable unit — enough for the bot to ask "which one?".</summary>
public sealed record GenesysEligibleUnitDto(
    int UnitId, string? UnitNumber, int ProjectId, string? ProjectName, int? Floor, string? BookingStatus);

/// <param name="UnitId">CRM unit id.</param>
/// <param name="UnitNumber">Unit number.</param>
/// <param name="Tower">Tower/building; null when CRM does not record it.</param>
/// <param name="Floor">Floor number.</param>
/// <param name="UnitType">CRM unit type code and, when known, its name.</param>
/// <param name="Bedrooms">Bedroom count, or null.</param>
/// <param name="Area">Area with its measurement unit, or null.</param>
/// <param name="Booking">The CRM Lead the unit was sold/contracted through.</param>
/// <param name="Parking">Parking spaces, or null when not recorded.</param>
/// <param name="ExpectedHandoverDate">Unit-level planned handover (ISO date), separate from the project's.</param>
/// <param name="ActualHandoverDate">Unit-level actual handover (ISO date), separate from the project's.</param>
public sealed record GenesysUnitDetailsDto(
    int UnitId,
    string? UnitNumber,
    string? Tower,
    int? Floor,
    GenesysUnitTypeDto UnitType,
    int? Bedrooms,
    GenesysAreaDto? Area,
    GenesysBookingDto Booking,
    IReadOnlyList<GenesysParkingDto>? Parking,
    string? ExpectedHandoverDate,
    string? ActualHandoverDate);

public sealed record GenesysUnitTypeDto(int Code, string? Name);

public sealed record GenesysAreaDto(decimal Value, string? Unit);

/// <param name="Reference">CRM's Lead id for this sale/contract, as text.</param>
/// <param name="Status">CRM's display name for the Lead status (e.g. "Sold", "Contract").</param>
/// <param name="StatusCode">CRM's Lead status code.</param>
public sealed record GenesysBookingDto(string Reference, string? Status, int StatusCode);

public sealed record GenesysParkingDto(string? Number, string? Level, string? Type);

/// <param name="ProjectId">CRM project id.</param>
/// <param name="Name">Project name (English).</param>
/// <param name="ArabicName">Project name (Arabic).</param>
/// <param name="Address">Location/address as recorded, or null.</param>
/// <param name="Status">Construction/project status as recorded, or null.</param>
/// <param name="ExpectedHandoverDate">Project-level planned handover (ISO date), separate from the unit's.</param>
/// <param name="ActualHandoverDate">Project-level actual handover (ISO date), only when recorded.</param>
/// <param name="Description">Customer-facing description, or null.</param>
/// <param name="Amenities">Customer-facing amenities, or null.</param>
/// <param name="CompletionPercentage">Construction completion, 0–100, as recorded by CRM; 0 is a genuine value, null means not recorded.</param>
/// <param name="ExpectedCompletionDate">Planned construction completion (ISO date). A different event from handover — never filled from, or into, the handover dates.</param>
/// <param name="ActualCompletionDate">Actual construction completion (ISO date), only when recorded.</param>
public sealed record GenesysProjectDetailsDto(
    int ProjectId,
    string? Name,
    string? ArabicName,
    string? Address,
    string? Status,
    string? ExpectedHandoverDate,
    string? ActualHandoverDate,
    string? Description,
    IReadOnlyList<string>? Amenities,
    decimal? CompletionPercentage = null,
    string? ExpectedCompletionDate = null,
    string? ActualCompletionDate = null);

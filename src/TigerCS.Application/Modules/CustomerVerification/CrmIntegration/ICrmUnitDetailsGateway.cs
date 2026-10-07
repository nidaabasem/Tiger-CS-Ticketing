namespace TigerCS.Application.Modules.CustomerVerification.CrmIntegration;

/// <summary>
/// Read-only port for the customer-facing unit and project facts that Tiger
/// CRM's one documented endpoint (<c>GET /TicketingSystem/GetBuyerByPhone</c>,
/// <see cref="ICrmBuyerLookupGateway"/>) does <b>not</b> return: tower,
/// bedrooms, area and its measurement unit, parking, the project's address,
/// status, description and amenities, and the expected/actual handover dates
/// at unit and project level.
///
/// <para>
/// <b>No wire contract is invented here.</b> Tiger CRM publishes no endpoint
/// for these facts, so the "Http" provider resolves
/// <c>UnimplementedCrmUnitDetailsGateway</c>, which answers
/// <see cref="CrmUnitDetailsOutcome.NotAvailable"/> — every enrichment value is
/// then null in the API response, never a guess. Replace that class once the
/// CRM team publishes the endpoint; the registration in
/// <c>IntegrationsServiceCollectionExtensions</c> is the only wiring.
/// </para>
///
/// <para>
/// <b>Not an authorization source.</b> This port is only ever called for a
/// unit that <c>CrmBuyerLookupAppService</c> has already shown belongs to the
/// verified customer. It decides nothing about access.
/// </para>
/// </summary>
public interface ICrmUnitDetailsGateway
{
    /// <summary>Never throws for an expected CRM answer; an unreachable CRM is <see cref="CrmUnitDetailsOutcome.Unavailable"/>.</summary>
    Task<CrmUnitDetailsResult> GetUnitDetailsAsync(int crmCustomerId, int crmUnitId, CancellationToken cancellationToken = default);
}

public enum CrmUnitDetailsOutcome
{
    /// <summary>CRM returned details for the unit (any individual value may still be null).</summary>
    Found,

    /// <summary>No source publishes these facts (or none for this unit). Every enrichment value is null.</summary>
    NotAvailable,

    /// <summary>The source exists but could not be reached. Every enrichment value is null.</summary>
    Unavailable
}

public sealed record CrmUnitDetailsResult(CrmUnitDetailsOutcome Outcome, CrmUnitDetails? Details = null)
{
    public static CrmUnitDetailsResult Found(CrmUnitDetails details) => new(CrmUnitDetailsOutcome.Found, details);

    public static CrmUnitDetailsResult NotAvailable() => new(CrmUnitDetailsOutcome.NotAvailable);

    public static CrmUnitDetailsResult Unavailable() => new(CrmUnitDetailsOutcome.Unavailable);
}

/// <summary>
/// Unit-level and project-level enrichment, exactly as the source recorded it.
/// A null is "not recorded" — never a default. Unit-level and project-level
/// handover dates are separate on purpose: the API returns them separately so
/// the bot can say which date applies.
/// </summary>
/// <param name="UnitTypeName">The unit type's display name (CRM's own code is carried separately by the buyer lookup).</param>
/// <param name="TowerName">Tower/building name.</param>
/// <param name="Bedrooms">Bedroom count; null when not recorded (a studio is not assumed to be 0).</param>
/// <param name="Area">Unit area value.</param>
/// <param name="AreaUnit">The measurement unit the area is recorded in (e.g. "sqft"); null when the source gives none — never assumed.</param>
/// <param name="Parking">Parking spaces; null when parking is not recorded, an empty list only when the source says there is none.</param>
/// <param name="UnitExpectedHandoverDate">Handover date recorded on the unit itself, planned.</param>
/// <param name="UnitActualHandoverDate">Handover date recorded on the unit itself, actual.</param>
/// <param name="Project">Project facts not in the buyer lookup.</param>
public sealed record CrmUnitDetails(
    string? UnitTypeName,
    string? TowerName,
    int? Bedrooms,
    decimal? Area,
    string? AreaUnit,
    IReadOnlyList<CrmParkingSpace>? Parking,
    DateOnly? UnitExpectedHandoverDate,
    DateOnly? UnitActualHandoverDate,
    CrmProjectDetails? Project);

public sealed record CrmParkingSpace(string? Number, string? Level, string? Type);

/// <param name="Address">Location/address as recorded.</param>
/// <param name="Status">Construction/project status as recorded.</param>
/// <param name="ExpectedHandoverDate">Project-level planned handover.</param>
/// <param name="ActualHandoverDate">Project-level actual handover, only when recorded.</param>
/// <param name="Description">Customer-facing description. Internal notes are never carried by this record.</param>
/// <param name="Amenities">Customer-facing amenity names; null when not recorded.</param>
public sealed record CrmProjectDetails(
    string? Address,
    string? Status,
    DateOnly? ExpectedHandoverDate,
    DateOnly? ActualHandoverDate,
    string? Description,
    IReadOnlyList<string>? Amenities);

namespace TigerCS.Application.Modules.CustomerVerification.Dto;

/// <summary>A CRM unit, as returned by the read-only CRM lookups (MVP-API-Contracts.md §2.1/§2.2).</summary>
/// <param name="UnitReferenceId">This system's local id for the unit. Use this value — not <paramref name="CrmUnitId"/> — when creating a verification session.</param>
/// <param name="CrmUnitId">The unit's identifier in Tiger CRM.</param>
/// <param name="UnitNumber">The unit number.</param>
/// <param name="PropertyName">The property the unit belongs to, when CRM records one.</param>
/// <param name="TowerName">The tower within the property, when CRM records one.</param>
/// <param name="UnitType">The unit type, when CRM records one.</param>
/// <param name="LastSyncedAtUtc">When this local copy was last refreshed from CRM, in UTC.</param>
/// <param name="ContactCount">How many contacts are linked to the unit.</param>
public sealed record UnitVerificationResponseDto(
    int UnitReferenceId,
    string CrmUnitId,
    string UnitNumber,
    string? PropertyName,
    string? TowerName,
    string? UnitType,
    DateTime LastSyncedAtUtc,
    int ContactCount);

/// <summary>A contact linked to a CRM unit (MVP-API-Contracts.md §2.3).</summary>
/// <param name="ContactReferenceId">This system's local id for the contact. Use this value when creating a verification session.</param>
/// <param name="CrmContactId">The contact's identifier in Tiger CRM.</param>
/// <param name="DisplayName">The contact's name, when CRM records one.</param>
/// <param name="ContactChannel">The contact's phone/email, when CRM records one.</param>
/// <param name="ContactType">One of Owner, Tenant, Representative.</param>
/// <param name="AuthorizedRepresentativeOfContactReferenceId">For a Representative, the contact they represent; null otherwise.</param>
public sealed record ContactVerificationResponseDto(
    int ContactReferenceId,
    string CrmContactId,
    string? DisplayName,
    string? ContactChannel,
    string ContactType,
    int? AuthorizedRepresentativeOfContactReferenceId);

public enum CrmLookupOutcome
{
    Success,
    NotFound,
    CrmUnavailable
}

public sealed record CrmUnitLookupResult(CrmLookupOutcome Outcome, UnitVerificationResponseDto? Response = null)
{
    public static CrmUnitLookupResult Success(UnitVerificationResponseDto response) => new(CrmLookupOutcome.Success, response);
    public static CrmUnitLookupResult NotFound() => new(CrmLookupOutcome.NotFound);
    public static CrmUnitLookupResult Unavailable() => new(CrmLookupOutcome.CrmUnavailable);
}

public sealed record CrmUnitSearchResult(CrmLookupOutcome Outcome, IReadOnlyList<UnitVerificationResponseDto>? Units = null)
{
    public static CrmUnitSearchResult Success(IReadOnlyList<UnitVerificationResponseDto> units) => new(CrmLookupOutcome.Success, units);
    public static CrmUnitSearchResult Unavailable() => new(CrmLookupOutcome.CrmUnavailable);
}

public sealed record CrmContactsLookupResult(CrmLookupOutcome Outcome, IReadOnlyList<ContactVerificationResponseDto>? Contacts = null)
{
    public static CrmContactsLookupResult Success(IReadOnlyList<ContactVerificationResponseDto> contacts) =>
        new(CrmLookupOutcome.Success, contacts);
    public static CrmContactsLookupResult NotFound() => new(CrmLookupOutcome.NotFound);
    public static CrmContactsLookupResult Unavailable() => new(CrmLookupOutcome.CrmUnavailable);
}

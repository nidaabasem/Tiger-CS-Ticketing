namespace TigerCS.Application.Modules.CrmVerification.Dto;

// MVP-API-Contracts.md §2.1/§2.2
public sealed record UnitVerificationResponseDto(
    int UnitReferenceId,
    string CrmUnitId,
    string UnitNumber,
    string? PropertyName,
    string? TowerName,
    string? UnitType,
    DateTime LastSyncedAtUtc,
    int ContactCount);

// MVP-API-Contracts.md §2.3
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

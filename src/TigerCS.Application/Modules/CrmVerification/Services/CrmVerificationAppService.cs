using TigerCS.Application.Modules.CrmVerification.Abstractions;
using TigerCS.Application.Modules.CrmVerification.Dto;
using TigerCS.Domain.Modules.CrmVerification;

namespace TigerCS.Application.Modules.CrmVerification.Services;

/// <summary>
/// MVP-API-Contracts.md §2.1-§2.3 — CRM unit/contact lookup, cache-aside
/// against <see cref="ICrmGateway"/> (ADR-0006): every lookup upserts the
/// local UnitReferences/ContactReferences display cache from the CRM's
/// response, never treating the cache itself as authoritative.
/// </summary>
public sealed class CrmVerificationAppService(
    ICrmGateway crmGateway,
    IUnitReferenceRepository unitRepository,
    IContactReferenceRepository contactRepository,
    ICrmVerificationUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<CrmUnitLookupResult> GetUnitAsync(string crmUnitId, CancellationToken cancellationToken = default)
    {
        CrmUnitResult? crmUnit;
        try
        {
            crmUnit = await crmGateway.GetUnitAsync(crmUnitId, cancellationToken);
        }
        catch (CrmGatewayUnavailableException)
        {
            return CrmUnitLookupResult.Unavailable();
        }

        if (crmUnit is null)
        {
            return CrmUnitLookupResult.NotFound();
        }

        var unitReference = await UpsertUnitAsync(crmUnit, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var contacts = await contactRepository.GetByUnitReferenceIdAsync(unitReference.UnitReferenceId, cancellationToken);
        return CrmUnitLookupResult.Success(ToDto(unitReference, contacts.Count));
    }

    public async Task<CrmUnitSearchResult> SearchUnitsAsync(
        string unitNumber, string? propertyName, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CrmUnitResult> matches;
        try
        {
            matches = await crmGateway.SearchUnitsAsync(unitNumber, propertyName, cancellationToken);
        }
        catch (CrmGatewayUnavailableException)
        {
            return CrmUnitSearchResult.Unavailable();
        }

        var unitReferences = new List<UnitReference>();
        foreach (var match in matches)
        {
            unitReferences.Add(await UpsertUnitAsync(match, cancellationToken));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        var results = new List<UnitVerificationResponseDto>();
        foreach (var unitReference in unitReferences)
        {
            var contacts = await contactRepository.GetByUnitReferenceIdAsync(unitReference.UnitReferenceId, cancellationToken);
            results.Add(ToDto(unitReference, contacts.Count));
        }

        return CrmUnitSearchResult.Success(results);
    }

    public async Task<CrmContactsLookupResult> GetContactsAsync(string crmUnitId, CancellationToken cancellationToken = default)
    {
        CrmUnitResult? crmUnit;
        IReadOnlyList<CrmContactResult> crmContacts;
        try
        {
            crmUnit = await crmGateway.GetUnitAsync(crmUnitId, cancellationToken);
            if (crmUnit is null)
            {
                return CrmContactsLookupResult.NotFound();
            }

            crmContacts = await crmGateway.GetContactsAsync(crmUnitId, cancellationToken);
        }
        catch (CrmGatewayUnavailableException)
        {
            return CrmContactsLookupResult.Unavailable();
        }

        var unitReference = await UpsertUnitAsync(crmUnit, cancellationToken);
        // Saved now so UnitReferenceId is assigned before contacts FK it below.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var contactReferences = new List<ContactReference>();
        foreach (var crmContact in crmContacts)
        {
            var contactReference = await UpsertContactAsync(unitReference.UnitReferenceId, crmContact, cancellationToken);
            contactReferences.Add(contactReference);

            // Saved per-contact (not once after the loop) so a later
            // contact's AuthorizedRepresentativeOfCrmContactId lookup can
            // resolve an earlier contact in this same batch — a repository
            // query only sees rows already committed, not pending adds.
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var dtos = contactReferences
            .Select(c => new ContactVerificationResponseDto(
                c.ContactReferenceId,
                c.CrmContactId,
                c.DisplayName,
                c.ContactChannel,
                c.ContactType.ToString(),
                c.AuthorizedRepresentativeOfContactReferenceId))
            .ToList();

        return CrmContactsLookupResult.Success(dtos);
    }

    private async Task<UnitReference> UpsertUnitAsync(CrmUnitResult crmUnit, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var existing = await unitRepository.GetByCrmUnitIdAsync(crmUnit.CrmUnitId, cancellationToken);
        if (existing is not null)
        {
            existing.RefreshFromCrm(crmUnit.UnitNumber, crmUnit.PropertyName, crmUnit.TowerName, crmUnit.UnitType, now);
            return existing;
        }

        var created = new UnitReference(
            crmUnit.CrmUnitId, crmUnit.UnitNumber, crmUnit.PropertyName, crmUnit.TowerName, crmUnit.UnitType, now);
        await unitRepository.AddAsync(created, cancellationToken);
        return created;
    }

    private async Task<ContactReference> UpsertContactAsync(
        int unitReferenceId, CrmContactResult crmContact, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        int? authorizedRepresentativeOfContactReferenceId = null;
        if (crmContact.AuthorizedRepresentativeOfCrmContactId is not null)
        {
            var representedContact = await contactRepository.GetByCrmContactIdAsync(
                crmContact.AuthorizedRepresentativeOfCrmContactId, cancellationToken);
            authorizedRepresentativeOfContactReferenceId = representedContact?.ContactReferenceId;
        }

        var existing = await contactRepository.GetByCrmContactIdAsync(crmContact.CrmContactId, cancellationToken);
        if (existing is not null)
        {
            existing.RefreshFromCrm(
                crmContact.DisplayName, crmContact.ContactChannel, crmContact.ContactType,
                authorizedRepresentativeOfContactReferenceId, now);
            return existing;
        }

        var created = new ContactReference(
            crmContact.CrmContactId, unitReferenceId, crmContact.DisplayName, crmContact.ContactChannel,
            crmContact.ContactType, authorizedRepresentativeOfContactReferenceId, now);
        await contactRepository.AddAsync(created, cancellationToken);
        return created;
    }

    private static UnitVerificationResponseDto ToDto(UnitReference unit, int contactCount) => new(
        unit.UnitReferenceId, unit.CrmUnitId, unit.UnitNumber, unit.PropertyName, unit.TowerName, unit.UnitType,
        unit.LastSyncedAtUtc, contactCount);
}

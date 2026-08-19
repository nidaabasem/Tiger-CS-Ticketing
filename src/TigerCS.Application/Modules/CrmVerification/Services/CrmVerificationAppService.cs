using TigerCS.Application.Modules.CrmVerification.Abstractions;
using TigerCS.Application.Modules.CrmVerification.Dto;
using TigerCS.Domain.Modules.CrmVerification;

namespace TigerCS.Application.Modules.CrmVerification.Services;

/// <summary>
/// MVP-API-Contracts.md §2.1-§2.3 — CRM unit/contact lookup, cache-aside
/// against <see cref="ICrmGateway"/> (ADR-0006): every lookup upserts the
/// local UnitReferences/ContactReferences display cache from the CRM's
/// response, never treating the cache itself as authoritative.
///
/// <para>
/// <b>Refresh/staleness.</b> There is no TTL-based expiry — every lookup
/// through this service (<see cref="GetUnitAsync"/>, <see cref="SearchUnitsAsync"/>,
/// <see cref="GetContactsAsync"/>) is a synchronous read-through: it always
/// calls <see cref="ICrmGateway"/> first and overwrites the cache row with
/// whatever the CRM returns (<c>UnitReference.RefreshFromCrm</c>/
/// <c>ContactReference.RefreshFromCrm</c>), then serves the caller the
/// just-refreshed data. The cache therefore can only be as stale as
/// "however long ago this exact unit/contact was last looked up" —
/// surfaced to every caller via <c>LastSyncedAtUtc</c> so staleness is never
/// silent. Nothing in this module reads the cache without going through the
/// gateway first (no cache-only lookup path exists).
/// </para>
/// <para>
/// <b>Data minimization.</b> Both cache tables and every response DTO in
/// this module carry only the fields FR-VER-01–04's verify-then-create
/// workflow actually needs for read-back/identification: unit
/// number/property/tower/type, and per-contact display name/channel/type/
/// representative-authorization link. No other CRM field is ever requested,
/// cached, or returned. <see cref="UnitVerificationResponseDto"/> (unit
/// lookup/search) deliberately carries no contact-level data at all — only
/// a <c>ContactCount</c> — so a unit search never leaks a customer's name,
/// contact channel, or contact type; that PII is returned only from the
/// dedicated, unit-scoped <see cref="GetContactsAsync"/> call, which FR-VER-04
/// requires so the agent can identify which specific contact is on the line.
/// </para>
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

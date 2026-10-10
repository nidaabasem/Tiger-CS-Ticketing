using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>A unit to link: its company and PACT unit code, plus the contact PACT has for it (may be empty).</summary>
public sealed record UnitToLink(int CompanyId, string UnitCode, string PactName, string PactMobile, string PactEmail);

/// <summary>
/// The one bulk way to attach CRM owners to units outside the SQL engines (Receivables rows, exports, Payment Summary): ONE set-based read of the local CRM owner data for any
/// number of units, then <see cref="CollectionsContactLinker"/>. There is never a CRM or PACT request per unit.
/// </summary>
public sealed class CollectionsUnitLinkService(ICollectionsCrmOwnerStore store)
{
    public async Task<IReadOnlyDictionary<(int CompanyId, string UnitCode), LinkedContact>> LinkAsync(IReadOnlyList<UnitToLink> units, CancellationToken cancellationToken)
    {
        var keys = units.Select(u => (u.CompanyId, Key: CollectionsUnitKey.Normalize(u.UnitCode))).Where(k => k.Key is not null).Select(k => (k.CompanyId, k.Key!)).Distinct().ToList();
        var links = await store.GetLinksAsync(keys, cancellationToken);
        var result = new Dictionary<(int, string), LinkedContact>();
        foreach (var unit in units)
        {
            var key = CollectionsUnitKey.Normalize(unit.UnitCode);
            var link = key is not null && links.TryGetValue((unit.CompanyId, key), out var found) ? found : null;
            result[(unit.CompanyId, unit.UnitCode)] = CollectionsContactLinker.Link(
                new SourceContact(unit.PactName.Trim(), CollectionsContactNormalizer.NormalizePhone(unit.PactMobile), CollectionsContactNormalizer.NormalizeEmail(unit.PactEmail)),
                link?.Contact ?? SourceContact.Empty, link?.Status ?? CrmLinkStatus.None);
        }
        return result;
    }
}

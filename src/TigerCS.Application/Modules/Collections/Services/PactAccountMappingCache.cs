using System.Collections.Concurrent;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>
/// The verified PACT account mapping for a customer: the tenant's own rows
/// from <c>v1/contracts/{mobile}</c> (and so its company/tenant pairs), as
/// discovered at <see cref="VerifiedAtUtc"/>. <see cref="Contracts"/> empty =
/// PACT answered but returned no contracts for the tenant.
/// </summary>
public sealed record PactAccountMapping(
    string TenantId,
    IReadOnlyList<PactContractDto> Contracts,
    string? MatchedMobile,
    DateTime VerifiedAtUtc);

/// <summary>
/// Keeps a verified PACT mapping for a bounded time, so Payment tab loads and
/// retries do not re-run contract discovery. <c>v1/contracts/{mobile}</c>
/// writes inside EDSM (contract §8.3), so each call needs a reason.
///
/// <para>
/// <b>What it is not:</b> an authorization cache. Every request still checks the
/// Collections financial-read grant and the Customer Directory's department
/// visibility before the cache is consulted. The cache holds only which
/// company/tenant pairs PACT returned for the tenant.
/// </para>
///
/// <para>
/// <b>Revalidation (discovery runs again) when:</b>
/// <list type="number">
/// <item>the entry is older than <c>CollectionsSource:PactMappingTtlMinutes</c>
/// (default 30, at most 24 h). Ownership is never reused indefinitely;</item>
/// <item>the customer's persisted identity changes. The key is the TigerCS
/// tenant plus the profile's phone numbers, so a new or changed number misses the
/// cache;</item>
/// <item>EDSM refuses a cached pair (business-rule or validation rejection)
/// and the entry is invalidated;</item>
/// <item>PACT could not be reached. Failures are never cached, so the next
/// load has a reason to try again.</item>
/// </list>
/// "PACT answered: no contracts for this tenant" is cached for
/// <c>PactMappingNegativeTtlMinutes</c> (default 5), so retries on an unmapped
/// customer don't each trigger a write.
/// </para>
///
/// <para>
/// In-process and per instance, like EDSM's own cache. A restart or a second
/// instance only means one more discovery.
/// </para>
/// </summary>
public sealed class PactAccountMappingCache(TimeProvider timeProvider)
{
    public const int MaxEntries = 5000;

    private readonly ConcurrentDictionary<string, PactAccountMapping> _entries = new(StringComparer.Ordinal);

    public static string Key(string tenantId, IEnumerable<string> phoneNumbers) =>
        tenantId + "|" + string.Join(",", phoneNumbers.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct().Order(StringComparer.Ordinal));

    public PactAccountMapping? Get(string key, TimeSpan ttl, TimeSpan negativeTtl)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            return null;
        }

        var age = timeProvider.GetUtcNow().UtcDateTime - entry.VerifiedAtUtc;
        var limit = entry.Contracts.Count == 0 ? negativeTtl : ttl;
        if (age < TimeSpan.Zero || age >= limit)
        {
            _entries.TryRemove(key, out _);
            return null;
        }

        return entry;
    }

    public void Set(string key, PactAccountMapping mapping)
    {
        if (_entries.Count >= MaxEntries)
        {
            // Bounded: drop the oldest tenth rather than grow without limit.
            foreach (var old in _entries.OrderBy(e => e.Value.VerifiedAtUtc).Take(MaxEntries / 10).ToList())
            {
                _entries.TryRemove(old.Key, out _);
            }
        }

        _entries[key] = mapping;
    }

    public void Invalidate(string key) => _entries.TryRemove(key, out _);
}

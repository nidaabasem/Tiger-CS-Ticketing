using System.Collections.Concurrent;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>What PACT's contract list holds for a set of phone numbers: every customer (tenant) found, each with ALL of that tenant's contracts / units, and how complete the answer is.</summary>
/// <param name="Customers">Every tenant PACT answered, with the phone that found it.</param>
/// <param name="Asked">How many phone numbers were asked.</param>
/// <param name="Failed">How many PACT could not answer (timeout, outage, key): the picture is then incomplete and nothing may be concluded from what is NOT in it.</param>
public sealed record PactContractDiscoveryResult(IReadOnlyList<(string Phone, PactCustomerMatchDto Customer)> Customers, int Asked, int Failed)
{
    public bool Complete => Failed == 0;
    public bool AnyAnswered => Asked - Failed > 0;
}

/// <summary>
/// The ONE place the unit statement and the CRM-project evidence ask PACT's <c>v1/contracts/{mobile}</c> (read-only for TigerCS, although EDSM records the call - contract §8.3):
/// one call per distinct phone (at most five), results kept for a short time so one Payment tab load never repeats a call. Failures are never cached.
/// </summary>
public sealed class PactContractDiscovery(IPactCustomerLookupGateway pact, TimeProvider timeProvider)
{
    private const int MaxPhones = 5;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<string, (DateTime AtUtc, PactCustomerLookupResult Result)> Cache = new();

    public async Task<PactContractDiscoveryResult> DiscoverAsync(IEnumerable<string> phones, CancellationToken cancellationToken)
    {
        var list = phones.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.Ordinal).Take(MaxPhones).ToList();
        var found = new List<(string, PactCustomerMatchDto)>();
        var failed = 0;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var phone in list)
        {
            PactCustomerLookupResult result;
            if (Cache.TryGetValue(phone, out var hit) && now - hit.AtUtc < Ttl) result = hit.Result;
            else
            {
                result = await pact.SearchByMobileAsync(phone, cancellationToken);
                if (result.Outcome is PactCustomerLookupOutcome.Success or PactCustomerLookupOutcome.NotFound) Cache[phone] = (now, result);
            }
            if (result.Outcome is PactCustomerLookupOutcome.Success or PactCustomerLookupOutcome.NotFound)
                foreach (var customer in result.Customers ?? []) found.Add((phone, customer));
            else failed++;
        }
        return new PactContractDiscoveryResult(found, list.Count, failed);
    }

    /// <summary>For tests: forget every cached answer.</summary>
    public static void ClearCache() => Cache.Clear();
}

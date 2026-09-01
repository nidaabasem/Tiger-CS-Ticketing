using TigerCS.Application.Modules.CustomerVerification.PactIntegration;

namespace TigerCS.Integrations.Modules.PactIntegration;

/// <summary>
/// Deterministic, in-memory fake implementing <see cref="IPactCustomerLookupGateway"/> —
/// a read-only, mobile-search-only customer/contract directory port. Holds no
/// verification state and makes no verification decision, matching
/// <see cref="IPactCustomerLookupGateway"/>'s own documented boundary.
///
/// <para>
/// <b>NOT PRODUCTION-READY — and no longer the only implementation.</b>
/// This fixture-backed double exists so <c>CustomerLookupAppService</c> and
/// the Ticketing integration tests run deterministically with no network;
/// the real HTTP-backed implementation is <see cref="PactCustomerHttpGateway"/>,
/// selected with <c>Pact:Provider = "Http"</c> plus the <c>PactApi</c>
/// configuration section. Never describe validation against this fixture as
/// production/PACT-integration-tested in any status update or go-live
/// communication (mirrors <c>MockCrmGateway</c>'s own disclaimer).
/// </para>
/// </summary>
public sealed class MockPactGateway : IPactCustomerLookupGateway
{
    /// <summary>Any input containing this token simulates a PACT outage — Failed-source testing.</summary>
    public const string OutageTrigger = "OUTAGE";

    private static readonly IReadOnlyDictionary<string, PactCustomerMatchDto> Fixtures =
        new Dictionary<string, PactCustomerMatchDto>(StringComparer.OrdinalIgnoreCase)
        {
            // Shaped like the real PactCustomerHttpGateway mapping: the ids
            // are PACT's numeric tenantID/unitID/contractID as strings, and
            // CustomerType is the raw customerBuyerType CODE (never display
            // text — see PactCustomerMatchDto.CustomerType's remarks).
            ["+971500000002"] = new PactCustomerMatchDto(
                "3001",
                "Fatima Noor",
                "+971500000002",
                Email: null,
                CustomerType: "2",
                Contracts:
                [
                    new PactContractDto("41230", "88001", "0304", "Tiger Marina Residences", "Residential")
                ])
        };

    public Task<PactCustomerLookupResult> SearchByMobileAsync(string mobileNumber, CancellationToken cancellationToken = default)
    {
        if (mobileNumber.Contains(OutageTrigger, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(PactCustomerLookupResult.Unavailable(
                $"Simulated PACT outage triggered by '{mobileNumber}' (MockPactGateway — a test double, never a real PACT failure)."));
        }

        return Task.FromResult(Fixtures.TryGetValue(mobileNumber, out var match)
            ? PactCustomerLookupResult.Success([match])
            : PactCustomerLookupResult.NotFound());
    }
}

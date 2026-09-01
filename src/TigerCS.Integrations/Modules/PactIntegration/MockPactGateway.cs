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
            ["+971500000002"] = new PactCustomerMatchDto(
                "PACT-CUST-3001",
                "Fatima Noor",
                "+971500000002",
                Email: null,
                CustomerType: "Tenant",
                Contracts:
                [
                    new PactContractDto("PACT-UNIT-A-0304", "PACT-CNT-88001", "0304", "Tiger Marina Residences", "Residential")
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

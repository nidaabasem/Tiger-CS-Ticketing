using TigerCS.Application.Modules.CustomerVerification.CustomerLookup;

namespace TigerCS.Integrations.Modules.PactIntegration;

/// <summary>
/// Deterministic, in-memory fake implementing <see cref="IPactGateway"/> —
/// a read-only, phone-search-only customer directory port. Holds no
/// verification state and makes no verification decision, matching
/// <see cref="IPactGateway"/>'s own documented boundary.
///
/// <para>
/// <b>NOT PRODUCTION-READY.</b> No real PACT endpoint details were available
/// to build against — this fixture-backed double exists solely so
/// <c>CustomerLookupAppService</c> can be built and tested end to end. It
/// must be replaced by a real HTTP-backed <see cref="IPactGateway"/>
/// implementation before any non-pilot use. Never describe validation
/// against this fixture as production/PACT-integration-tested in any status
/// update or go-live communication (mirrors <c>MockCrmGateway</c>'s own
/// disclaimer).
/// </para>
/// </summary>
public sealed class MockPactGateway : IPactGateway
{
    /// <summary>Any input containing this token simulates a PACT outage — Failed-source testing.</summary>
    public const string OutageTrigger = "OUTAGE";

    private static readonly IReadOnlyDictionary<string, PactCustomerMatch> Fixtures =
        new Dictionary<string, PactCustomerMatch>(StringComparer.OrdinalIgnoreCase)
        {
            ["+971500000002"] = new PactCustomerMatch("PACT-CUST-3001", "Fatima Noor", "+971500000002")
        };

    public Task<PactCustomerMatch?> SearchByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default)
    {
        if (phoneNumber.Contains(OutageTrigger, StringComparison.OrdinalIgnoreCase))
        {
            throw new PactGatewayUnavailableException(
                $"Simulated PACT outage triggered by '{phoneNumber}' (MockPactGateway — a test double, never a real PACT failure).");
        }

        return Task.FromResult(Fixtures.GetValueOrDefault(phoneNumber));
    }
}

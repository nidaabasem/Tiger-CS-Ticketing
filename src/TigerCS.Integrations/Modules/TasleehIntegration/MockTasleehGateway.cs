using TigerCS.Application.Modules.CustomerVerification.CustomerLookup;

namespace TigerCS.Integrations.Modules.TasleehIntegration;

/// <summary>
/// Deterministic, in-memory fake implementing <see cref="ITasleehGateway"/> —
/// a read-only, phone-search-only customer directory port. Holds no
/// verification state and makes no verification decision, matching
/// <see cref="ITasleehGateway"/>'s own documented boundary.
///
/// <para>
/// <b>NOT PRODUCTION-READY.</b> No real Tasleeh endpoint details were
/// available to build against — this fixture-backed double exists solely so
/// <c>CustomerLookupAppService</c> can be built and tested end to end. It
/// must be replaced by a real HTTP-backed <see cref="ITasleehGateway"/>
/// implementation before any non-pilot use. Never describe validation
/// against this fixture as production/Tasleeh-integration-tested in any
/// status update or go-live communication (mirrors <c>MockCrmGateway</c>'s
/// own disclaimer).
/// </para>
/// </summary>
public sealed class MockTasleehGateway : ITasleehGateway
{
    /// <summary>Any input containing this token simulates a Tasleeh outage — Failed-source testing.</summary>
    public const string OutageTrigger = "OUTAGE";

    private static readonly IReadOnlyDictionary<string, TasleehCustomerMatch> Fixtures =
        new Dictionary<string, TasleehCustomerMatch>(StringComparer.OrdinalIgnoreCase)
        {
            ["+971500000003"] = new TasleehCustomerMatch("TSL-CUST-4001", "Omar Khalid", "+971500000003")
        };

    public Task<IReadOnlyList<TasleehCustomerMatch>> SearchByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default)
    {
        if (phoneNumber.Contains(OutageTrigger, StringComparison.OrdinalIgnoreCase))
        {
            throw new TasleehGatewayUnavailableException(
                $"Simulated Tasleeh outage triggered by '{phoneNumber}' (MockTasleehGateway — a test double, never a real Tasleeh failure).");
        }

        return Task.FromResult<IReadOnlyList<TasleehCustomerMatch>>(
            Fixtures.TryGetValue(phoneNumber, out var match) ? [match] : []);
    }
}

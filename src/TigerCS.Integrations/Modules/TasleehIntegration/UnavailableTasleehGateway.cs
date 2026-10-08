using TigerCS.Application.Modules.CustomerVerification.CustomerLookup;

namespace TigerCS.Integrations.Modules.TasleehIntegration;

/// <summary>
/// <c>Tasleeh:Provider = "Unavailable"</c>. No Tasleeh endpoint, authentication or payload contract has been approved, so a real
/// gateway cannot be built. This implementation never invents a match: every search reports the source as unavailable, which the
/// customer search surfaces as a failed Tasleeh source while CRM and PACT keep working. Use it in UAT/production instead of
/// <c>Mock</c>, whose fixture customer must never be shown for a real caller.
/// </summary>
public sealed class UnavailableTasleehGateway : ITasleehGateway
{
    public Task<IReadOnlyList<TasleehCustomerMatch>> SearchByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default) =>
        throw new TasleehGatewayUnavailableException(
            "Tasleeh integration is not configured: no approved Tasleeh API contract exists yet (Tasleeh:Provider=Unavailable).");
}

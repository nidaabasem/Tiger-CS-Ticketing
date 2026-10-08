using TigerCS.Application.Modules.CustomerVerification.CustomerLookup;
using TigerCS.Integrations.Modules.TasleehIntegration;

namespace TigerCS.Tests.CustomerVerification.Services;

public sealed class UnavailableTasleehGatewayTests
{
    [Theory]
    [InlineData("+971500000003")] // the Mock's fixture number must NOT produce a match
    [InlineData("+971501234567")]
    public async Task EverySearch_ReportsTheSourceUnavailable_NeverAMatch(string phone) =>
        await Assert.ThrowsAsync<TasleehGatewayUnavailableException>(
            () => new UnavailableTasleehGateway().SearchByPhoneAsync(phone));
}

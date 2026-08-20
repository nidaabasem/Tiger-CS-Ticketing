using TigerCS.Integrations.Modules.CrmIntegration;

namespace TigerCS.Tests.CustomerVerification.Services;

/// <summary>
/// Pure unit tests for the extracted startup-guard logic Program.cs calls
/// (<see cref="CrmGatewaySafety.IsUnsafe"/>) — proves the guard is
/// conditional on the selected gateway type, not simply the environment
/// name, per the final correction requiring focused tests for this.
/// </summary>
public class CrmGatewaySafetyTests
{
    [Theory]
    [InlineData("Mock", "Production")]
    [InlineData("Mock", "Staging")]
    [InlineData("Mock", "UAT")]
    [InlineData("mock", "Production")] // case-insensitive provider match
    public void IsUnsafe_MockProviderOutsideAllowedEnvironments_ReturnsTrue(string provider, string environmentName)
    {
        Assert.True(CrmGatewaySafety.IsUnsafe(provider, environmentName));
    }

    [Theory]
    [InlineData("Mock", "Development")]
    [InlineData("Mock", "Testing")]
    public void IsUnsafe_MockProviderInsideAllowedEnvironments_ReturnsFalse(string provider, string environmentName)
    {
        Assert.False(CrmGatewaySafety.IsUnsafe(provider, environmentName));
    }

    /// <summary>
    /// The crux of "conditional on gateway type, not environment name":
    /// a non-Mock provider is never flagged unsafe by this guard, in any
    /// environment including Production — a future real InternalCrmGateway
    /// must be able to run in UAT/Production once configured.
    /// </summary>
    [Theory]
    [InlineData("InternalCrmGateway", "Production")]
    [InlineData("InternalCrmGateway", "UAT")]
    [InlineData("InternalCrmGateway", "Development")]
    [InlineData("InternalCrmGateway", "Testing")]
    public void IsUnsafe_NonMockProviderInAnyEnvironment_ReturnsFalse(string provider, string environmentName)
    {
        Assert.False(CrmGatewaySafety.IsUnsafe(provider, environmentName));
    }

    [Fact]
    public void IsUnsafe_NullProvider_ReturnsFalse()
    {
        Assert.False(CrmGatewaySafety.IsUnsafe(null, "Production"));
    }
}

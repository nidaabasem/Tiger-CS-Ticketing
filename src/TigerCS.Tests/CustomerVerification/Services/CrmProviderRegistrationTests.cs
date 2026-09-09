using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.CustomerLookup;
using TigerCS.Integrations.Modules.CrmIntegration;

namespace TigerCS.Tests.CustomerVerification.Services;

/// <summary>
/// The Crm:Provider contract across environments: "Http" (Development, UAT,
/// Production) never resolves MockCrmGateway — the two ports Tiger CRM
/// publishes no endpoint for fail closed instead — while "Mock" (Testing)
/// resolves the fixture gateway, and the real CRM Buyer Lookup port is
/// governed by neither. The committed appsettings carry the standard
/// provider and no CRM secret.
/// </summary>
public sealed class CrmProviderRegistrationTests
{
    private static ServiceProvider BuildIntegrations(string? provider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Crm:Provider"] = provider })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTigerCsIntegrations(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void HttpProvider_ResolvesTheFailClosedGateway_NeverTheMock_ForBothPorts()
    {
        using var provider = BuildIntegrations("Http");
        using var scope = provider.CreateScope();

        var unitLookup = scope.ServiceProvider.GetRequiredService<ICrmGateway>();
        var phoneSearch = scope.ServiceProvider.GetRequiredService<ICrmCustomerLookupGateway>();

        Assert.IsType<UnimplementedCrmHttpGateway>(unitLookup);
        Assert.IsType<UnimplementedCrmHttpGateway>(phoneSearch);
        Assert.IsNotType<MockCrmGateway>(unitLookup);
        Assert.IsNotType<MockCrmGateway>(phoneSearch);
    }

    [Fact]
    public void MockProvider_ResolvesOneMockCrmGateway_BehindBothPorts()
    {
        using var provider = BuildIntegrations("Mock");
        using var scope = provider.CreateScope();

        var unitLookup = scope.ServiceProvider.GetRequiredService<ICrmGateway>();
        var phoneSearch = scope.ServiceProvider.GetRequiredService<ICrmCustomerLookupGateway>();

        Assert.IsType<MockCrmGateway>(unitLookup);
        Assert.Same(unitLookup, phoneSearch);
    }

    [Theory]
    [InlineData("InternalCrmGateway")]
    [InlineData("http")]
    [InlineData("")]
    public void UnsupportedProvider_StillThrowsOnResolve_NeverFallsBackToTheMock(string provider)
    {
        using var serviceProvider = BuildIntegrations(provider);
        using var scope = serviceProvider.CreateScope();

        var ex = Assert.Throws<NotSupportedException>(() => scope.ServiceProvider.GetRequiredService<ICrmGateway>());
        Assert.Contains("Crm:Provider", ex.Message);
        Assert.Throws<NotSupportedException>(() => scope.ServiceProvider.GetRequiredService<ICrmCustomerLookupGateway>());
    }

    [Fact]
    public async Task HttpProvider_UnitAndContactOperations_FailClosedThroughTheOutageContract()
    {
        using var provider = BuildIntegrations("Http");
        using var scope = provider.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<ICrmGateway>();

        var getUnit = await Assert.ThrowsAsync<CrmGatewayUnavailableException>(() => gateway.GetUnitAsync("CRM-UNIT-1001"));
        var search = await Assert.ThrowsAsync<CrmGatewayUnavailableException>(() => gateway.SearchUnitsAsync("1204", null));
        var contacts = await Assert.ThrowsAsync<CrmGatewayUnavailableException>(() => gateway.GetContactsAsync("CRM-UNIT-1001"));

        foreach (var ex in new[] { getUnit, search, contacts })
        {
            Assert.Contains("Tiger CRM", ex.Message);
            Assert.Contains("no published endpoint", ex.Message);
        }
    }

    [Fact]
    public async Task HttpProvider_PhoneSearch_FailsClosedThroughTheOutageContract()
    {
        using var provider = BuildIntegrations("Http");
        using var scope = provider.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<ICrmCustomerLookupGateway>();

        var ex = await Assert.ThrowsAsync<CrmCustomerLookupGatewayUnavailableException>(() => gateway.SearchByPhoneAsync("+971509990001"));

        Assert.Contains("Tiger CRM", ex.Message);
        Assert.Contains("no published endpoint", ex.Message);
    }

    [Theory]
    [InlineData("Http")]
    [InlineData("Mock")]
    public void BuyerLookup_IsAlwaysTheRealHttpGateway_WhateverTheProvider(string provider)
    {
        using var serviceProvider = BuildIntegrations(provider);
        using var scope = serviceProvider.CreateScope();

        Assert.IsType<CrmBuyerHttpGateway>(scope.ServiceProvider.GetRequiredService<ICrmBuyerLookupGateway>());
    }

    [Fact]
    public void ProviderDefaultsToHttp_SoAMissingKeyNeverMeansFixtureData()
    {
        Assert.Equal("Http", new CrmGatewayOptions().Provider);
    }

    // ---- the committed Api configuration: standard provider, no CRM secret ----

    private static string ApiSettingsFile(string fileName, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", "..", ".."));
        return Path.Combine(srcDir, "TigerCS.Api", fileName);
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void CommittedApiSettings_UseHttp_AndCarryNoCrmSecretKey(string fileName)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(ApiSettingsFile(fileName)),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        var crm = document.RootElement.GetProperty("Crm");

        Assert.Equal("Http", crm.GetProperty("Provider").GetString());
        // Crm:SecretKey comes from user-secrets or the Crm__SecretKey
        // environment variable (docs/DEV-SETUP.md §3a) — never from a
        // committed file.
        Assert.False(
            crm.EnumerateObject().Any(p => string.Equals(p.Name, "SecretKey", StringComparison.OrdinalIgnoreCase)),
            $"{fileName} must not carry Crm:SecretKey");
    }
}

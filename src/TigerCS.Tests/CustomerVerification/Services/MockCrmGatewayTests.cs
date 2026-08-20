using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Integrations.Modules.CrmIntegration;

namespace TigerCS.Tests.CustomerVerification.Services;

/// <summary>Backlog S-06: the fake ICrmGateway implementation's happy path and simulated-outage path.</summary>
public class MockCrmGatewayTests
{
    [Fact]
    public async Task GetUnitAsync_KnownFixture_ReturnsUnit()
    {
        var gateway = new MockCrmGateway();

        var unit = await gateway.GetUnitAsync("CRM-UNIT-1001");

        Assert.NotNull(unit);
        Assert.Equal("1204", unit!.UnitNumber);
    }

    [Fact]
    public async Task GetUnitAsync_UnknownId_ReturnsNull()
    {
        var gateway = new MockCrmGateway();

        var unit = await gateway.GetUnitAsync("CRM-DOES-NOT-EXIST");

        Assert.Null(unit);
    }

    [Fact]
    public async Task GetUnitAsync_OutageTriggerId_ThrowsCrmGatewayUnavailable()
    {
        var gateway = new MockCrmGateway();

        await Assert.ThrowsAsync<CrmGatewayUnavailableException>(
            () => gateway.GetUnitAsync($"CRM-UNIT-{MockCrmGateway.OutageTrigger}"));
    }

    [Fact]
    public async Task SearchUnitsAsync_MatchingUnitNumber_ReturnsFixture()
    {
        var gateway = new MockCrmGateway();

        var results = await gateway.SearchUnitsAsync("0507", propertyName: null);

        Assert.Single(results);
        Assert.Equal("CRM-UNIT-1002", results[0].CrmUnitId);
    }

    [Fact]
    public async Task GetContactsAsync_UnitWithRepresentative_ReturnsAuthorizationLink()
    {
        var gateway = new MockCrmGateway();

        var contacts = await gateway.GetContactsAsync("CRM-UNIT-1002");

        var representative = Assert.Single(contacts, c => c.AuthorizedRepresentativeOfCrmContactId is not null);
        Assert.Equal("CRM-CONTACT-2003", representative.AuthorizedRepresentativeOfCrmContactId);
    }
}

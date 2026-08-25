using TigerCS.Application.Modules.CustomerVerification.CustomerLookup;
using TigerCS.Integrations.Modules.CrmIntegration;

namespace TigerCS.Tests.CustomerVerification.Services;

/// <summary>
/// Business-rule change: CRM Buyer lookup by phone — MockCrmGateway's own
/// grouping/eligibility rules for <see cref="ICrmCustomerLookupGateway.SearchByPhoneAsync"/>.
/// Never assumes one phone number resolves to one customer, or one customer
/// owns one unit; only <c>ContactType.Owner</c> relationships are eligible
/// units (this integration's real, existing ownership signal — see
/// <see cref="MockCrmGateway.SearchByPhoneAsync"/>'s remarks for why there is
/// no Lead/deal-status field to filter by instead).
/// </summary>
public class MockCrmGatewayPhoneSearchTests
{
    [Fact]
    public async Task SearchByPhoneAsync_OneCustomerOneEligibleUnit_ReturnsSingleCustomerWithSingleUnit()
    {
        var gateway = new MockCrmGateway();

        var matches = await gateway.SearchByPhoneAsync("+971509990001");

        var customer = Assert.Single(matches);
        Assert.Equal("Sami Nasser", customer.DisplayName);
        var unit = Assert.Single(customer.Units);
        Assert.Equal("CRM-UNIT-1107", unit.CrmUnitId);
    }

    [Fact]
    public async Task SearchByPhoneAsync_OneCustomerMultipleEligibleUnits_ReturnsAllOfThatCustomersUnits()
    {
        var gateway = new MockCrmGateway();

        var matches = await gateway.SearchByPhoneAsync("+971501234567");

        var ahmedAli = Assert.Single(matches, c => c.ExternalCustomerId == "CRM-CUST-5001");
        Assert.Equal(2, ahmedAli.Units.Count);
        Assert.Contains(ahmedAli.Units, u => u.CrmUnitId == "CRM-UNIT-1101");
        Assert.Contains(ahmedAli.Units, u => u.CrmUnitId == "CRM-UNIT-1102");
    }

    [Fact]
    public async Task SearchByPhoneAsync_SamePhoneMatchesMultipleDistinctCustomers_ReturnsBoth()
    {
        var gateway = new MockCrmGateway();

        var matches = await gateway.SearchByPhoneAsync("+971501234567");

        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, c => c.ExternalCustomerId == "CRM-CUST-5001");
        Assert.Contains(matches, c => c.DisplayName == "Ahmad Ali Hassan");
    }

    [Fact]
    public async Task SearchByPhoneAsync_MultipleCustomers_EachKeepsOnlyItsOwnUnits()
    {
        var gateway = new MockCrmGateway();

        var matches = await gateway.SearchByPhoneAsync("+971501234567");

        var ahmedAli = Assert.Single(matches, c => c.ExternalCustomerId == "CRM-CUST-5001");
        var ahmadHassan = Assert.Single(matches, c => c.DisplayName == "Ahmad Ali Hassan");
        Assert.DoesNotContain(ahmadHassan.Units, u => ahmedAli.Units.Select(x => x.CrmUnitId).Contains(u.CrmUnitId));
        Assert.Equal("CRM-UNIT-1103", Assert.Single(ahmadHassan.Units).CrmUnitId);
    }

    [Fact]
    public async Task SearchByPhoneAsync_CustomerHasOnlyTenantRelationship_FoundWithNoEligibleUnits()
    {
        var gateway = new MockCrmGateway();

        var matches = await gateway.SearchByPhoneAsync("+971502223333");

        var customer = Assert.Single(matches);
        Assert.Equal("Khalid Nasser", customer.DisplayName);
        Assert.Empty(customer.Units);
    }

    [Fact]
    public async Task SearchByPhoneAsync_CustomerHasOwnerAndTenantRelationships_OnlyOwnerUnitIncluded()
    {
        var gateway = new MockCrmGateway();

        var matches = await gateway.SearchByPhoneAsync("+971503334444");

        var customer = Assert.Single(matches);
        Assert.Equal("Mona Youssef", customer.DisplayName);
        var unit = Assert.Single(customer.Units);
        Assert.Equal("CRM-UNIT-1105", unit.CrmUnitId);
        Assert.DoesNotContain(customer.Units, u => u.CrmUnitId == "CRM-UNIT-1106");
    }

    [Fact]
    public async Task SearchByPhoneAsync_DuplicateRelationshipRowForSameUnit_DoesNotDuplicateTheUnit()
    {
        var gateway = new MockCrmGateway();

        var matches = await gateway.SearchByPhoneAsync("+971505556666");

        var customer = Assert.Single(matches);
        Assert.Single(customer.Units);
    }

    [Fact]
    public async Task SearchByPhoneAsync_UnknownPhone_ReturnsEmpty()
    {
        var gateway = new MockCrmGateway();

        var matches = await gateway.SearchByPhoneAsync("+971500009999");

        Assert.Empty(matches);
    }

    [Fact]
    public async Task SearchByPhoneAsync_OutageTrigger_ThrowsCrmCustomerLookupGatewayUnavailable()
    {
        var gateway = new MockCrmGateway();

        await Assert.ThrowsAsync<CrmCustomerLookupGatewayUnavailableException>(
            () => gateway.SearchByPhoneAsync($"+9715{MockCrmGateway.OutageTrigger}0000"));
    }
}

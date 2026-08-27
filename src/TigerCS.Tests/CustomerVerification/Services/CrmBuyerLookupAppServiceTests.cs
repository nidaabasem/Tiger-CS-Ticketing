using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Tests.CustomerVerification.Fakes;

namespace TigerCS.Tests.CustomerVerification.Services;

/// <summary>
/// CRM Buyer Lookup: <see cref="CrmBuyerLookupAppService"/>'s own business
/// rule — re-applying the Sold(8)/Contract(9) + Buyer(1) validity check
/// rather than trusting the gateway's (CRM's) filtering unconditionally —
/// plus straight pass-through of every non-Success outcome.
/// </summary>
public class CrmBuyerLookupAppServiceTests
{
    private static CrmCustomerDto Customer(int id = 1) => new(id, $"Buyer {id}", null, "+971500000000", null);

    private static CrmBuyerUnitDto Unit(int leadStatus = 8, int customerType = 1, int unitId = 500) => new(
        LeadId: 100, LeadStatus: leadStatus, LeadStatusName: "Sold", UnitId: unitId, UnitNumber: "1204",
        UnitStatus: 3, UnitType: 2, FloorNumber: 12, ProjectId: 79, ProjectName: "Tiger Tower",
        ProjectArabicName: null, CustomerType: customerType, CustomerTypeName: "Buyer");

    [Fact]
    public async Task GetBuyerByPhoneAsync_ValidSoldUnit_PassesThroughAsSuccess()
    {
        var gateway = new FakeCrmBuyerLookupGateway().Returns(
            CrmBuyerLookupResult.Success([new CrmBuyerMatchDto(Customer(), [Unit(leadStatus: 8)])]));
        var service = new CrmBuyerLookupAppService(gateway);

        var result = await service.GetBuyerByPhoneAsync("+971500000000");

        Assert.Equal(CrmBuyerLookupOutcome.Success, result.Outcome);
        Assert.Single(result.Buyers!.Single().Units);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_ValidContractUnit_PassesThroughAsSuccess()
    {
        var gateway = new FakeCrmBuyerLookupGateway().Returns(
            CrmBuyerLookupResult.Success([new CrmBuyerMatchDto(Customer(), [Unit(leadStatus: 9)])]));
        var service = new CrmBuyerLookupAppService(gateway);

        var result = await service.GetBuyerByPhoneAsync("+971500000000");

        Assert.Equal(CrmBuyerLookupOutcome.Success, result.Outcome);
    }

    [Theory]
    [InlineData(1)]  // New
    [InlineData(2)]  // some other non-Sold/Contract status
    [InlineData(10)]
    public async Task GetBuyerByPhoneAsync_UnitWithInvalidLeadStatus_IsFilteredOut(int invalidLeadStatus)
    {
        var gateway = new FakeCrmBuyerLookupGateway().Returns(
            CrmBuyerLookupResult.Success([new CrmBuyerMatchDto(Customer(), [Unit(leadStatus: invalidLeadStatus)])]));
        var service = new CrmBuyerLookupAppService(gateway);

        var result = await service.GetBuyerByPhoneAsync("+971500000000");

        // The only buyer had only an invalid unit — dropping it leaves no buyers, so the overall outcome becomes NotFound.
        Assert.Equal(CrmBuyerLookupOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_UnitWithNonBuyerCustomerType_IsFilteredOut()
    {
        var gateway = new FakeCrmBuyerLookupGateway().Returns(
            CrmBuyerLookupResult.Success([new CrmBuyerMatchDto(Customer(), [Unit(customerType: 2)])])); // e.g. Tenant, out of scope this phase
        var service = new CrmBuyerLookupAppService(gateway);

        var result = await service.GetBuyerByPhoneAsync("+971500000000");

        Assert.Equal(CrmBuyerLookupOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_BuyerWithMixOfValidAndInvalidUnits_KeepsOnlyValidUnits()
    {
        var gateway = new FakeCrmBuyerLookupGateway().Returns(
            CrmBuyerLookupResult.Success(
            [
                new CrmBuyerMatchDto(Customer(), [Unit(leadStatus: 8, unitId: 500), Unit(leadStatus: 1, unitId: 501)])
            ]));
        var service = new CrmBuyerLookupAppService(gateway);

        var result = await service.GetBuyerByPhoneAsync("+971500000000");

        Assert.Equal(CrmBuyerLookupOutcome.Success, result.Outcome);
        var buyer = Assert.Single(result.Buyers!);
        var unit = Assert.Single(buyer.Units);
        Assert.Equal(500, unit.UnitId);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_MultipleBuyersOneWithOnlyInvalidUnits_DropsOnlyThatBuyer()
    {
        var gateway = new FakeCrmBuyerLookupGateway().Returns(
            CrmBuyerLookupResult.Success(
            [
                new CrmBuyerMatchDto(Customer(1), [Unit(leadStatus: 8)]),
                new CrmBuyerMatchDto(Customer(2), [Unit(leadStatus: 1)])
            ]));
        var service = new CrmBuyerLookupAppService(gateway);

        var result = await service.GetBuyerByPhoneAsync("+971500000000");

        Assert.Equal(CrmBuyerLookupOutcome.Success, result.Outcome);
        var buyer = Assert.Single(result.Buyers!);
        Assert.Equal(1, buyer.Customer.CustomerId);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_MultipleUnitsAndMultipleBuyers_PreservesAllValidOnes()
    {
        var gateway = new FakeCrmBuyerLookupGateway().Returns(
            CrmBuyerLookupResult.Success(
            [
                new CrmBuyerMatchDto(Customer(1), [Unit(leadStatus: 8, unitId: 500), Unit(leadStatus: 9, unitId: 501)]),
                new CrmBuyerMatchDto(Customer(2), [Unit(leadStatus: 8, unitId: 600)])
            ]));
        var service = new CrmBuyerLookupAppService(gateway);

        var result = await service.GetBuyerByPhoneAsync("+971500000000");

        Assert.Equal(CrmBuyerLookupOutcome.Success, result.Outcome);
        Assert.Equal(2, result.Buyers!.Count);
        Assert.Equal(2, result.Buyers!.Single(b => b.Customer.CustomerId == 1).Units.Count);
        Assert.Single(result.Buyers!.Single(b => b.Customer.CustomerId == 2).Units);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_GatewayNotFound_PassesThrough()
    {
        var gateway = new FakeCrmBuyerLookupGateway().Returns(CrmBuyerLookupResult.NotFound());
        var service = new CrmBuyerLookupAppService(gateway);

        var result = await service.GetBuyerByPhoneAsync("+971500000000");

        Assert.Equal(CrmBuyerLookupOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_GatewayUnauthorized_PassesThrough()
    {
        var gateway = new FakeCrmBuyerLookupGateway().Returns(CrmBuyerLookupResult.Unauthorized());
        var service = new CrmBuyerLookupAppService(gateway);

        var result = await service.GetBuyerByPhoneAsync("+971500000000");

        Assert.Equal(CrmBuyerLookupOutcome.Unauthorized, result.Outcome);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_GatewayUnavailable_PassesThrough()
    {
        var gateway = new FakeCrmBuyerLookupGateway().Returns(CrmBuyerLookupResult.Unavailable());
        var service = new CrmBuyerLookupAppService(gateway);

        var result = await service.GetBuyerByPhoneAsync("+971500000000");

        Assert.Equal(CrmBuyerLookupOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task GetBuyerByPhoneAsync_PassesPhoneNumberThroughToGatewayUnchanged()
    {
        var gateway = new FakeCrmBuyerLookupGateway().Returns(CrmBuyerLookupResult.NotFound());
        var service = new CrmBuyerLookupAppService(gateway);

        await service.GetBuyerByPhoneAsync("+971 50 000 0000");

        Assert.Equal("+971 50 000 0000", gateway.LastSearchedPhoneNumber);
    }
}

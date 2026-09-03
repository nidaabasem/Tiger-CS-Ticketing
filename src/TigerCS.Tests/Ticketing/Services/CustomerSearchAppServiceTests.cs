using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// The Customer Workspace's standalone phone search. It must return exactly
/// what the underlying sources return — the same CRM Buyer Lookup and
/// PACT/Tasleeh legs the New Ticket wizard uses — with each source's outcome
/// reported independently: one source failing never hides another's match,
/// and no intake record or ticket is ever touched.
/// </summary>
public class CustomerSearchAppServiceTests
{
    private const string Phone = "+971501112233";

    private sealed record Fixture(
        CustomerSearchAppService Service,
        FakeCrmBuyerLookupGateway CrmBuyers,
        FakePactCustomerLookupGateway Pact,
        FakeTasleehGateway Tasleeh);

    private static Fixture CreateService()
    {
        var crmBuyerGateway = new FakeCrmBuyerLookupGateway();
        var crmBuyerLookup = new CrmBuyerLookupAppService(crmBuyerGateway, NullLogger<CrmBuyerLookupAppService>.Instance);

        var intakeRecords = new FakeIntakeRecordRepository();
        var departmentSources = new FakeDepartmentCustomerLookupSourceRepository();
        var crmLookup = new FakeCrmCustomerLookupGateway();
        var pact = new FakePactCustomerLookupGateway();
        var tasleeh = new FakeTasleehGateway();
        var crmUnitLookup = new CrmUnitLookupAppService(
            new FakeCrmGateway(), new FakeUnitReferenceRepository(), new FakeContactReferenceRepository(),
            new FakeCustomerVerificationUnitOfWork(), TimeProvider.System);
        var customerLookup = new CustomerLookupAppService(
            intakeRecords, departmentSources, crmLookup, pact, tasleeh, crmUnitLookup);

        return new Fixture(new CustomerSearchAppService(crmBuyerLookup, customerLookup), crmBuyerGateway, pact, tasleeh);
    }

    private static CrmBuyerMatchDto Buyer(int customerId, string name) => new(
        new CrmCustomerDto(customerId, name, null, Phone, "buyer@example.com"),
        [new CrmBuyerUnitDto(1, 4, "Contract", 101, "1506", 1, 2, 15, 10, "Nobles Tower", null, 1, "Buyer")]);

    [Fact]
    public async Task SearchByPhoneAsync_CrmMatch_ReturnsTheExactBuyerCrmMatched()
    {
        var f = CreateService();
        f.CrmBuyers.Returns(CrmBuyerLookupResult.Success([Buyer(9001, "Sami Nasser")]));

        var result = await f.Service.SearchByPhoneAsync(Phone);

        Assert.Equal("Found", result.CrmStatus);
        var buyer = Assert.Single(result.CrmBuyers);
        Assert.Equal(9001, buyer.Customer.CustomerId);
        Assert.Equal(Phone, f.CrmBuyers.LastSearchedPhoneNumber);
    }

    [Fact]
    public async Task SearchByPhoneAsync_PactMatch_ReturnsThePactCustomerWithItsStableExternalId()
    {
        var f = CreateService();
        f.Pact.Seed(Phone, new PactCustomerMatchDto(
            "PACT-CUST-77", "Aisha Rahman", Phone, null, "Tenant",
            [new PactContractDto("PACT-UNIT-5", "C-100", "1506", "Marina Heights", "Apartment")]));

        var result = await f.Service.SearchByPhoneAsync(Phone);

        Assert.Equal("NotFound", result.CrmStatus);
        var pactSource = Assert.Single(result.ExternalSources, s => s.Source == "Pact");
        Assert.Equal("Found", pactSource.Status);
        var customer = Assert.Single(pactSource.Customers);
        Assert.Equal("PACT-CUST-77", customer.ExternalCustomerId);
        Assert.Equal("1506", Assert.Single(customer.Units).UnitNumber);
    }

    [Fact]
    public async Task SearchByPhoneAsync_OneSourceFailing_NeverHidesAnotherSourcesMatch()
    {
        var f = CreateService();
        f.CrmBuyers.Returns(CrmBuyerLookupResult.Unavailable());
        f.Pact.Seed(Phone, new PactCustomerMatchDto("PACT-CUST-77", "Aisha Rahman", Phone, null, null, []));
        f.Tasleeh.ThrowUnavailable = true;

        var result = await f.Service.SearchByPhoneAsync(Phone);

        Assert.Equal("Failed", result.CrmStatus);
        Assert.Equal("Found", result.ExternalSources.Single(s => s.Source == "Pact").Status);
        Assert.Equal("Failed", result.ExternalSources.Single(s => s.Source == "Tasleeh").Status);
    }

    [Fact]
    public async Task SearchByPhoneAsync_AmbiguousCrmMatch_IsReportedDistinctly_WithNoBuyerAutoSelected()
    {
        var f = CreateService();
        f.CrmBuyers.Returns(CrmBuyerLookupResult.AmbiguousCustomerMatch());

        var result = await f.Service.SearchByPhoneAsync(Phone);

        Assert.Equal("AmbiguousMatch", result.CrmStatus);
        Assert.Empty(result.CrmBuyers);
    }

    [Fact]
    public async Task SearchByPhoneAsync_NoSourceMatches_ReturnsEverySourcesNotFound()
    {
        var f = CreateService();

        var result = await f.Service.SearchByPhoneAsync(Phone);

        Assert.Equal("NotFound", result.CrmStatus);
        Assert.Empty(result.CrmBuyers);
        Assert.All(result.ExternalSources, s => Assert.Equal("NotFound", s.Status));
    }
}

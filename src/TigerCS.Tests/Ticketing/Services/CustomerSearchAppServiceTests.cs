using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.Notifications.Fakes;
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

    private static Fixture CreateService(TimeProvider? timeProvider = null)
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
            intakeRecords, departmentSources, crmLookup, pact, tasleeh, crmUnitLookup, timeProvider ?? TimeProvider.System);

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

    // ---------------------------------------------------------------
    // PACT contract expiry — the workspace must still FIND a customer
    // whose contracts have all ended (historical tickets, payments and
    // fines live behind that identity), with the expired contracts
    // labelled rather than hidden. The New Ticket wizard's intake-anchored
    // lookup is the one that excludes them (CustomerLookupAppServiceTests).
    // Clock: 21:30 UTC on 5 October = 01:30 on 6 October in Dubai.
    // ---------------------------------------------------------------

    private static readonly DateTime DubaiBoundaryUtc = new(2026, 10, 5, 21, 30, 0, DateTimeKind.Utc);
    private static readonly DateOnly DubaiToday = new(2026, 10, 6);

    [Fact]
    public async Task SearchByPhoneAsync_PactCustomerWithEveryContractExpired_IsStillFound_WithContractsFlaggedExpired()
    {
        var f = CreateService(new FakeTimeProvider(DubaiBoundaryUtc));
        f.Pact.Seed(Phone, new PactCustomerMatchDto(
            "PACT-CUST-77", "Aisha Rahman", Phone, null, "Tenant",
            [
                new PactContractDto("PACT-UNIT-5", "C-100", "1506", "Marina Heights", "Apartment", ContractEndDate: DubaiToday.AddDays(-1)),
                new PactContractDto("PACT-UNIT-6", "C-101", "0802", "Marina Heights", "Apartment", ContractEndDate: new DateOnly(2021, 3, 31))
            ]));

        var result = await f.Service.SearchByPhoneAsync(Phone);

        var pactSource = Assert.Single(result.ExternalSources, s => s.Source == "Pact");
        Assert.Equal("Found", pactSource.Status);
        var customer = Assert.Single(pactSource.Customers);
        Assert.Equal("PACT-CUST-77", customer.ExternalCustomerId);
        Assert.Equal(2, customer.Units.Count);
        Assert.All(customer.Units, u => Assert.True(u.IsContractExpired));
        Assert.Equal(DubaiToday.AddDays(-1), customer.Units.Single(u => u.ExternalUnitId == "PACT-UNIT-5").ContractEndDate);
    }

    [Fact]
    public async Task SearchByPhoneAsync_PactMixedContracts_ReturnsAllUnits_FlaggingOnlyTheExpiredOnes()
    {
        var f = CreateService(new FakeTimeProvider(DubaiBoundaryUtc));
        f.Pact.Seed(Phone, new PactCustomerMatchDto(
            "PACT-CUST-77", "Aisha Rahman", Phone, null, "Tenant",
            [
                new PactContractDto("U-EXPIRED", "C-1", "1506", "Marina Heights", "Apartment", ContractEndDate: DubaiToday.AddDays(-1)),
                new PactContractDto("U-TODAY", "C-2", "1507", "Marina Heights", "Apartment", ContractEndDate: DubaiToday),
                new PactContractDto("U-FUTURE", "C-3", "1508", "Marina Heights", "Apartment", ContractEndDate: DubaiToday.AddYears(1)),
                new PactContractDto("U-NO-DATE", "C-4", "1509", "Marina Heights", "Apartment")
            ]));

        var result = await f.Service.SearchByPhoneAsync(Phone);

        var customer = Assert.Single(Assert.Single(result.ExternalSources, s => s.Source == "Pact").Customers);
        Assert.Equal(4, customer.Units.Count);
        Assert.Equal(["U-EXPIRED"], customer.Units.Where(u => u.IsContractExpired).Select(u => u.ExternalUnitId).ToArray());
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

using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;

namespace TigerCS.Tests.Ticketing.Services;

public sealed class CustomerIdentityLinkerTests
{
    private static CrmBuyerMatchDto Buyer(int id = 7) => new(
        new CrmCustomerDto(id, "Fatima Noor", null, "+971500000002", "fatima@example.test"),
        [new CrmBuyerUnitDto(1, 8, "Sold", 101, "0304", 1, 1, 3, 9, "Tiger Marina", null, 1, "Buyer")]);
    private static CustomerLookupCustomerDto Pact(string id = "3001") => new(id, "Fatima Noor", "971500000002", "fatima@example.test", "2",
        [new CustomerLookupUnitDto("9000", "0304", "Tiger Marina", null, "Residential", null, null, ContractEndDate: new DateOnly(2020, 1, 1), CompanyId: 25)]);
    private static CustomerLookupCustomerDto? Link(CrmBuyerMatchDto buyer, params CustomerLookupCustomerDto[] pact) =>
        CustomerIdentityLinker.FindPactMatch(buyer, [buyer], [CustomerLookupSourceResultDto.Found("Pact", pact)]);

    [Fact]
    public void ExactPersonAndPropertyUnit_WithNormalizedPhone_RetainsExpiredContracts()
    {
        var pact = Pact();
        Assert.Same(pact, Link(Buyer(), pact));
        Assert.Equal(new DateOnly(2020, 1, 1), Link(Buyer(), pact)!.Units.Single().ContractEndDate);
        Assert.Equal(25, Link(Buyer(), pact)!.Units.Single().CompanyId);
    }

    [Fact]
    public void PhoneAlone_IsNeverEnough() => Assert.Null(Link(Buyer(), Pact() with { DisplayName = "Another Person", Email = null }));

    [Fact]
    public void PersonAndPhoneWithoutTheSameUnit_IsNotEnough() => Assert.Null(Link(Buyer(), Pact() with { Units = [] }));

    [Fact]
    public void SameUnitNumberInADifferentProject_IsNotAMatch() => Assert.Null(Link(Buyer(), Pact() with
        { Units = [Pact().Units[0] with { PropertyName = "Another Tower" }] }));

    [Fact]
    public void SameRawUnitIdWithoutDisplayEvidence_IsNotAMatch() => Assert.Null(Link(Buyer(), Pact() with
        { Units = [Pact().Units[0] with { ExternalUnitId = "101", UnitNumber = null }] }));

    [Fact]
    public void TwoPactTenantsWithTheSameEvidence_StaySeparate() => Assert.Null(Link(Buyer(), Pact(), Pact("3002")));

    [Fact]
    public void TwoCrmCustomersMatchingOneTenant_StaySeparate() => Assert.Null(CustomerIdentityLinker.FindPactMatch(
        Buyer(), [Buyer(), Buyer(8)], [CustomerLookupSourceResultDto.Found("Pact", [Pact()])]));

    [Fact]
    public void MissingPhone_IsNeverInferredFromTheSearch() => Assert.Null(Link(Buyer(), Pact() with { PhoneNumber = null }));
}

using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Crm;

public sealed class CollectionsLeasingSummaryTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);

    private static PactContractDto C(string unit, int company = 7, string? contract = "C1", DateOnly? end = null) => new(unit, contract, "U" + unit, "Proj", "Residential", company, end);

    private static PactCustomerLookupResult Found(params PactCustomerMatchDto[] customers) => PactCustomerLookupResult.Success(customers);

    [Theory]
    [InlineData("+971501234567")]
    [InlineData("971501234567")]
    [InlineData("00971501234567")]
    [InlineData("0501234567")]
    public void TheMobileFormats_AreOneNumber(string input) => Assert.Equal("+971501234567", CollectionsContactNormalizer.NormalizePhone(input));

    [Fact]
    public void SeveralUnits_AreSeparateEntries_WithTheirOwnKeys_AndNoMixedAmounts()
    {
        var s = CollectionsLeasingSummaryAppService.Compose("+971501234567", Today, Found(
            new PactCustomerMatchDto("T1", "Tenant One", "971501234567", null, "1", [C("10", contract: "A"), C("11", contract: "B"), C("10", contract: "A")])));
        Assert.Equal("Found", s.LookupStatus);
        Assert.Equal(["10", "11"], s.Contracts.Select(c => c.UnitId));
        Assert.All(s.Contracts, c => { Assert.Equal(7, c.CompanyId); Assert.Equal("T1", c.PactTenantId); Assert.Null(c.Total); Assert.Equal("NoFinancialData", c.FinancialStatus); });
    }

    [Fact]
    public void EndedContracts_FormerTenants_AndOtherCompanies_AreNotUsed()
    {
        var s = CollectionsLeasingSummaryAppService.Compose("+971501234567", Today, Found(
            new PactCustomerMatchDto("T1", "Former", null, null, null, [C("10", end: new(2026, 9, 30))]),
            new PactCustomerMatchDto("T2", "Current", null, null, null, [C("10", end: new(2027, 1, 1)), C("20", company: 4)])));
        var only = Assert.Single(s.Contracts);
        Assert.Equal("T2", only.PactTenantId);
        Assert.Equal(1, s.ExcludedEndedContracts); Assert.Equal(1, s.ExcludedOtherCompanies);
    }

    [Fact]
    public void NoLeasingContract_IsNotFound_NotZero()
    {
        var s = CollectionsLeasingSummaryAppService.Compose("+971501234567", Today, Found(new PactCustomerMatchDto("T1", "X", null, null, null, [C("10", company: 4)])));
        Assert.Equal("NotFound", s.LookupStatus); Assert.Empty(s.Contracts);
        Assert.Equal("NotFound", CollectionsLeasingSummaryAppService.Compose("+971501234567", Today, PactCustomerLookupResult.NotFound()).LookupStatus);
    }
}

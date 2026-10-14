using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Genesys call-pickup unit lists: expired PACT contracts are projected out,
/// CRM units carry a unitCode, and the screen-pop list is de-duplicated only
/// on a resolved unit identity. All data is synthetic.
/// </summary>
public class GenesysCustomerLookupUnitTests
{
    private const string Phone = "+971500000001";

    private static DateOnly Today => PactContractActivity.TodayInDubai(TimeProvider.System);

    private static CrmBuyerUnitDto Crm(int unitId, string project, string? code, string? number, int projectId = 10) =>
        new(1, 4, "Contract", unitId, number, 1, 2, null, projectId, project, null, 1, "Buyer", code);

    private static PactContractDto Pact(string project, string unit, DateOnly? end, string? code = null, string id = "u1") =>
        new(id, "C-" + id, unit, project, null, 1, end, code);

    private static GenesysServiceFixture Seed(IEnumerable<CrmBuyerUnitDto> crm, params PactContractDto[] pact)
    {
        var f = new GenesysServiceFixture();
        f.CrmBuyers.Returns(CrmBuyerLookupResult.Success(
            [new CrmBuyerMatchDto(new CrmCustomerDto(1, "Test Buyer", null, Phone, null), [.. crm])]));
        if (pact.Length > 0)
        {
            f.Pact.Seed(Phone, new PactCustomerMatchDto("T1", "Test Buyer", Phone, null, null, [.. pact]));
        }

        return f;
    }

    private static CrmBuyerUnitDto[] RegressionCrm() =>
    [
        Crm(1, "Guzel Towers", "TP130", "902-B", 1),
        Crm(2, "Al Ghaf Tower", "TP140", "1906", 2),
        Crm(3, "Auresta Tower", "TP132", "705", 3)
    ];

    [Fact]
    public async Task Regression_ExpiredPactUnitsAreExcluded_AllCrmUnitsRemain_NoExpiredDuplicate()
    {
        var past = Today.AddDays(-1);
        var f = Seed(RegressionCrm(),
            Pact("Guzel Tower", "902-B", past, "TP130-902-B", "a"),
            Pact("Auresta Tower", "705", past, "TP132-705", "b"));

        var r = (await f.CustomerLookup.LookUpAsync(Phone))!;

        Assert.Equal(3, Assert.Single(r.CrmBuyers).Units.Count);
        Assert.Equal(["TP130-902-B", "TP140-1906", "TP132-705"], r.CrmBuyers[0].Units.Select(u => u.UnitCode));
        var pact = r.ExternalSources.Single(s => s.Source == "Pact");
        Assert.Empty(Assert.Single(pact.Customers).Units);
        Assert.Equal(
            ["Guzel Towers - 902-B", "Al Ghaf Tower - 1906", "Auresta Tower - 705"], r.ScreenPop.Units);
        Assert.Equal("Guzel Towers - 902-B; Al Ghaf Tower - 1906; Auresta Tower - 705", r.ScreenPop.UnitsText);
        Assert.All(r.ScreenPop.SelectableUnits, u => Assert.True(u.PaymentLookupReady));
    }

    [Fact]
    public async Task PactContracts_ActiveEndingTodayAndMissingDate_AreKept_ExpiredIsDropped()
    {
        var f = Seed([],
            Pact("Alpha Tower", "1", Today.AddDays(30), "TP201-1", "a"),
            Pact("Beta Tower", "2", Today, "TP202-2", "b"),
            Pact("Gamma Tower", "3", null, "TP203-3", "c"),
            Pact("Delta Tower", "4", Today.AddDays(-1), "TP204-4", "d"));

        var r = (await f.CustomerLookup.LookUpAsync(Phone))!;

        var units = r.ExternalSources.Single(s => s.Source == "Pact").Customers.Single().Units;
        Assert.Equal(["1", "2", "3"], units.Select(u => u.UnitNumber));
        Assert.Equal(["Alpha Tower - 1", "Beta Tower - 2", "Gamma Tower - 3"], r.ScreenPop.Units);
    }

    [Fact]
    public async Task PactCustomerWithOnlyExpiredContracts_IsStillCounted()
    {
        var f = Seed([], Pact("Delta Tower", "4", Today.AddDays(-5), "TP204-4"));

        var r = (await f.CustomerLookup.LookUpAsync(Phone))!;

        Assert.True(r.Found);
        Assert.Equal(1, r.ScreenPop.MatchedCustomerCount);
        Assert.Empty(r.ScreenPop.Units);
        Assert.Empty(r.ScreenPop.SelectableUnits);
    }

    [Theory]
    [InlineData("TP130", "902-B", "TP130-902-B")]
    [InlineData("TP140", "1906", "TP140-1906")]
    [InlineData("130", "902-b", "TP130-902-B")]
    [InlineData(" tp132 ", " 705 ", "TP132-705")]
    [InlineData(null, "902-B", null)]
    [InlineData("", "902-B", null)]
    [InlineData("Guzel", "902-B", null)]
    [InlineData("TP", "902-B", null)]
    [InlineData("TP130", null, null)]
    [InlineData("TP130", "  ", null)]
    public void CrmUnitCode_PreservesSuffixes_AndNeverFabricates(string? project, string? unit, string? expected) =>
        Assert.Equal(expected, CrmUnitCode.From(project, unit));

    [Fact]
    public void CrmUnitCode_IsNotDerivedFromProjectId() =>
        Assert.Null(Crm(1, "Guzel Towers", null, "902-B", projectId: 130).UnitCode);

    [Fact]
    public async Task ConfirmedDuplicate_IsListedOnce_WithTheCrmLabel_AndBothSources()
    {
        var f = Seed([Crm(1, "Guzel Towers", "TP130", "902-B")],
            Pact("Guzel Tower", "902-B", Today.AddDays(10), "tp130-902-b"));

        var r = (await f.CustomerLookup.LookUpAsync(Phone))!;

        Assert.Equal(["Guzel Towers - 902-B"], r.ScreenPop.Units);
        var unit = Assert.Single(r.ScreenPop.SelectableUnits);
        Assert.Equal("TP130-902-B", unit.UnitCode);
        Assert.Equal(["Crm", "Pact"], unit.Sources);
        Assert.Equal(2, r.ScreenPop.MatchedCustomerCount);   // people are never merged
        Assert.Equal(2, unit.Customers.Count);
    }

    [Fact]
    public async Task SameUnitNumberInDifferentTowers_AreNotMerged()
    {
        var f = Seed([Crm(1, "Guzel Towers", "TP130", "101")],
            Pact("Guzel Tower", "101", Today.AddDays(10), "TP999-101"));

        var r = (await f.CustomerLookup.LookUpAsync(Phone))!;

        Assert.Equal(2, r.ScreenPop.Units.Count);
        Assert.Equal(["TP130-101", "TP999-101"], r.ScreenPop.SelectableUnits.Select(u => u.UnitCode));
    }

    [Fact]
    public async Task SimilarTowerNamesWithoutCodes_AreNotMerged_AndAreNotPaymentReady()
    {
        var f = Seed([Crm(1, "Guzel Towers", null, "101")],
            Pact("Guzel Tower", "101", Today.AddDays(10), code: null));

        var r = (await f.CustomerLookup.LookUpAsync(Phone))!;

        Assert.Equal(["Guzel Towers - 101", "Guzel Tower - 101"], r.ScreenPop.Units);
        Assert.All(r.ScreenPop.SelectableUnits, u =>
        {
            Assert.Equal(string.Empty, u.UnitCode);
            Assert.False(u.PaymentLookupReady);
        });
    }

    [Fact]
    public async Task IdenticalLabelsWithoutResolvedCodes_AreNotMerged_AndKeepTheirSourceAndCustomer()
    {
        var f = Seed([Crm(1, "Guzel Towers", null, "101")],
            Pact("Guzel Towers", "101", Today.AddDays(10), code: null));

        var r = (await f.CustomerLookup.LookUpAsync(Phone))!;

        Assert.Equal(2, r.ScreenPop.SelectableUnits.Count);
        Assert.All(r.ScreenPop.SelectableUnits, u =>
        {
            Assert.Equal("Guzel Towers - 101", u.Label);
            Assert.False(u.PaymentLookupReady);
        });
        Assert.Equal([("Crm", "1")], r.ScreenPop.SelectableUnits[0].Customers.Select(c => (c.Source, c.ExternalCustomerId)));
        Assert.Equal([("Pact", "T1")], r.ScreenPop.SelectableUnits[1].Customers.Select(c => (c.Source, c.ExternalCustomerId)));
        Assert.Equal(["Crm"], r.ScreenPop.SelectableUnits[0].Sources);
        Assert.Equal(["Pact"], r.ScreenPop.SelectableUnits[1].Sources);
        Assert.Equal("Crm:1", r.ScreenPop.SelectableUnits[0].CustomersText);
        Assert.Equal("Pact:T1", r.ScreenPop.SelectableUnits[1].CustomersText);
    }

    [Fact]
    public async Task CrmUnitWithMissingProjectCode_HasNoUnitCode_AndKeepsTheOldLabel()
    {
        var f = Seed([Crm(1, "Guzel Towers", null, "902-B")]);

        var r = (await f.CustomerLookup.LookUpAsync(Phone))!;

        Assert.Null(r.CrmBuyers[0].Units[0].UnitCode);
        Assert.Equal(["Guzel Towers - 902-B"], r.ScreenPop.Units);
        Assert.False(Assert.Single(r.ScreenPop.SelectableUnits).PaymentLookupReady);
    }

    [Fact]
    public async Task TwoPeopleSharingAPhone_StayTwoCustomers_EvenWhenTheyShareAUnit()
    {
        var f = new GenesysServiceFixture();
        f.CrmBuyers.Returns(CrmBuyerLookupResult.Success(
            [new CrmBuyerMatchDto(new CrmCustomerDto(1, "Buyer One", null, Phone, null), [Crm(1, "Guzel Towers", "TP130", "902-B")])]));
        f.Pact.Seed(Phone, new PactCustomerMatchDto("T1", "Tenant One", Phone, null, null,
            [Pact("Guzel Tower", "902-B", null, "TP130-902-B")]));
        f.Pact.Seed(Phone, new PactCustomerMatchDto("T2", "Tenant Two", Phone, null, null,
            [Pact("Guzel Tower", "902-B", null, "TP130-902-B", "z")]));

        var r = (await f.CustomerLookup.LookUpAsync(Phone))!;

        Assert.Equal(3, r.ScreenPop.MatchedCustomerCount);
        var unit = Assert.Single(r.ScreenPop.SelectableUnits);
        // One unit, but every record it was found under stays attached; a code being resolved says nothing about who the caller is.
        Assert.Equal(
            [("Crm", "1"), ("Pact", "T1"), ("Pact", "T2")],
            unit.Customers.Select(c => (c.Source, c.ExternalCustomerId)));
        Assert.True(unit.PaymentLookupReady);
        Assert.Equal(["Crm", "Pact"], unit.Sources);
    }
}

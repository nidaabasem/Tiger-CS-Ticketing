using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.GenesysIntegration;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// The unit-details API's rules, with CRM controlled: a customer only ever
/// sees their own units, a unit id alone grants nothing, several units are
/// offered rather than guessed between, absent CRM data is null, and unit-
/// and project-level handover dates stay separate.
/// </summary>
public sealed class GenesysCustomerUnitDetailsTests
{
    private const int CustomerId = 9001;
    private const string Phone = "tel:+971500000900";

    private static CrmBuyerUnitDto Unit(int unitId, string number, int projectId, string project, int lead) =>
        new(LeadId: lead, LeadStatus: 4, LeadStatusName: "Contract", UnitId: unitId, UnitNumber: number, UnitStatus: 3,
            UnitType: 2, FloorNumber: 12, ProjectId: projectId, ProjectName: project, ProjectArabicName: null,
            CustomerType: 1, CustomerTypeName: "Buyer");

    private static CrmBuyerLookupResult Buyer(int customerId, params CrmBuyerUnitDto[] units) =>
        CrmBuyerLookupResult.Success([new CrmBuyerMatchDto(new CrmCustomerDto(customerId, "Test Buyer", null, "+971500000900", "private@example.test"), units)]);

    private sealed class Harness
    {
        public FakeCrmBuyerLookupGateway Crm { get; } = new();
        public FakeCrmUnitDetailsGateway Details { get; } = new();
        public GenesysOptions Options { get; } = new() { Enabled = true };

        public GenesysCustomerUnitDetailsAppService Service =>
            new(Options, new CrmBuyerLookupAppService(Crm, NullLogger<CrmBuyerLookupAppService>.Instance), Details,
                NullLogger<GenesysCustomerUnitDetailsAppService>.Instance);
    }

    private static Harness TwoUnitCustomer() => new Harness().With(h => h.Crm.Returns(Buyer(CustomerId,
        Unit(9200, "1204", 79, "Tiger Tower", 9100),
        Unit(9201, "0507", 80, "Tiger Heights", 9101))));

    // ---- authorization ----

    [Fact]
    public async Task OwnUnit_ReturnsDetails_AndAsksCrmByTheVerifiedNumber()
    {
        var h = TwoUnitCustomer();

        var result = await h.Service.GetAsync("crm:9001", Phone, 9200);

        Assert.Equal(GenesysUnitDetailsOutcome.UnitDetails, result.Outcome);
        Assert.Equal("+971500000900", h.Crm.LastSearchedPhoneNumber);
        var unit = result.Response!.Unit!;
        Assert.Equal(9200, unit.UnitId);
        Assert.Equal("1204", unit.UnitNumber);
        Assert.Equal(12, unit.Floor);
        Assert.Equal("9100", unit.Booking.Reference);
        Assert.Equal("Contract", unit.Booking.Status);
        Assert.Equal(79, result.Response.Project!.ProjectId);
        Assert.Equal("Tiger Tower", result.Response.Project.Name);
    }

    [Fact]
    public async Task AnotherCustomersUnit_IsRefused_AndNeverReachesTheDetailsSource()
    {
        var h = TwoUnitCustomer();

        // 9999 exists in CRM for somebody else; the customer's own list does not hold it.
        var result = await h.Service.GetAsync("crm:9001", Phone, 9999);

        Assert.Equal(GenesysUnitDetailsOutcome.UnitNotEligible, result.Outcome);
        Assert.Null(result.Response);
        Assert.Empty(h.Details.Calls);
    }

    [Fact]
    public async Task UnitThatDoesNotExist_IsRefusedIdenticallyToAnotherCustomersUnit()
    {
        var h = TwoUnitCustomer();

        var other = await h.Service.GetAsync("crm:9001", Phone, 9999);
        var missing = await h.Service.GetAsync("crm:9001", Phone, 1);

        Assert.Equal(other.Outcome, missing.Outcome);
    }

    [Fact]
    public async Task CustomerReferenceThatIsNotTheCustomerBehindTheNumber_IsRefused()
    {
        var h = TwoUnitCustomer();

        // Customer 7777 is claimed, but CRM resolves this number to 9001: their units are not disclosed.
        var result = await h.Service.GetAsync("crm:7777", Phone, 9200);

        Assert.Equal(GenesysUnitDetailsOutcome.CustomerNotVerified, result.Outcome);
        Assert.Null(result.Response);
        Assert.Empty(h.Details.Calls);
    }

    [Fact]
    public async Task NumberCrmDoesNotKnow_IsNotVerified()
    {
        var h = new Harness();
        h.Crm.Returns(CrmBuyerLookupResult.NotFound());

        var result = await h.Service.GetAsync("crm:9001", Phone, 9200);

        Assert.Equal(GenesysUnitDetailsOutcome.CustomerNotVerified, result.Outcome);
    }

    [Fact]
    public async Task TwoCustomersOnOneNumber_DiscloseNothing()
    {
        var h = new Harness();
        h.Crm.Returns(CrmBuyerLookupResult.AmbiguousCustomerMatch());

        var result = await h.Service.GetAsync("crm:9001", Phone, 9200);

        Assert.Equal(GenesysUnitDetailsOutcome.AmbiguousCustomer, result.Outcome);
        Assert.Null(result.Response);
    }

    [Theory]
    [InlineData(CrmBuyerLookupOutcome.Unavailable)]
    [InlineData(CrmBuyerLookupOutcome.Unauthorized)]
    [InlineData(CrmBuyerLookupOutcome.InvalidResponse)]
    public async Task CrmFailures_AreReportedAsUnavailable_NeverAsAnEmptyAnswer(CrmBuyerLookupOutcome crmOutcome)
    {
        var h = new Harness();
        h.Crm.Returns(new CrmBuyerLookupResult(crmOutcome));

        var result = await h.Service.GetAsync("crm:9001", Phone, 9200);

        Assert.Equal(GenesysUnitDetailsOutcome.CrmUnavailable, result.Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ext:Pact:55")]
    [InlineData("phone:971500000900")]
    [InlineData("crm:0")]
    [InlineData("abc")]
    [InlineData("-5")]
    public async Task MalformedOrNonCrmCustomerReference_IsRejectedBeforeAnyLookup(string? reference)
    {
        var h = TwoUnitCustomer();

        var result = await h.Service.GetAsync(reference, Phone, 9200);

        Assert.Equal(GenesysUnitDetailsOutcome.CustomerReferenceInvalid, result.Outcome);
        Assert.Equal(0, h.Crm.CallCount);
    }

    [Fact]
    public async Task PlainCrmCustomerId_AsReturnedByTheLookup_IsAccepted()
    {
        var h = TwoUnitCustomer();

        Assert.Equal(GenesysUnitDetailsOutcome.UnitDetails, (await h.Service.GetAsync("9001", Phone, 9200)).Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("tel:anonymous")]
    public async Task WithheldOrMissingNumber_IsRejectedBeforeAnyLookup(string? phone)
    {
        var h = TwoUnitCustomer();

        var result = await h.Service.GetAsync("crm:9001", phone, 9200);

        Assert.Equal(GenesysUnitDetailsOutcome.PhoneNumberInvalid, result.Outcome);
        Assert.Equal(0, h.Crm.CallCount);
    }

    [Fact]
    public async Task NonPositiveUnitId_IsRejected()
    {
        var h = TwoUnitCustomer();

        Assert.Equal(GenesysUnitDetailsOutcome.UnitIdInvalid, (await h.Service.GetAsync("crm:9001", Phone, 0)).Outcome);
    }

    [Fact]
    public async Task IntegrationSwitchedOff_AnswersDisabled_AndTouchesNothing()
    {
        var h = TwoUnitCustomer();
        h.Options.Enabled = false;

        var result = await h.Service.GetAsync("crm:9001", Phone, 9200);

        Assert.Equal(GenesysUnitDetailsOutcome.IntegrationDisabled, result.Outcome);
        Assert.Equal(0, h.Crm.CallCount);
    }

    // ---- multiple-unit selection ----

    [Fact]
    public async Task NoUnitSelected_ReturnsEveryEligibleUnit_WithoutDetails()
    {
        var h = TwoUnitCustomer();

        var result = await h.Service.GetAsync("crm:9001", Phone, null);

        Assert.Equal(GenesysUnitDetailsOutcome.UnitSelectionRequired, result.Outcome);
        var response = result.Response!;
        Assert.Equal("UnitSelectionRequired", response.Mode);
        Assert.Equal("crm:9001", response.CustomerReference);
        Assert.Equal([9200, 9201], response.EligibleUnits.Select(u => u.UnitId));
        Assert.Equal(["Tiger Tower", "Tiger Heights"], response.EligibleUnits.Select(u => u.ProjectName));
        Assert.Null(response.Unit);
        Assert.Null(response.Project);
        Assert.Empty(h.Details.Calls);
    }

    [Fact]
    public async Task SelectingOneOfSeveralUnits_ReturnsThatUnitsDetailsOnly()
    {
        var h = TwoUnitCustomer();

        var result = await h.Service.GetAsync("crm:9001", Phone, 9201);

        var response = result.Response!;
        Assert.Equal("UnitDetails", response.Mode);
        Assert.Equal(9201, response.Unit!.UnitId);
        Assert.Equal("0507", response.Unit.UnitNumber);
        Assert.Equal("Tiger Heights", response.Project!.Name);
        Assert.Empty(response.EligibleUnits);
        Assert.Equal([(CustomerId, 9201)], h.Details.Calls);
    }

    [Fact]
    public async Task NothingBeyondTheMappedFields_IsExposed()
    {
        var h = TwoUnitCustomer();

        var json = System.Text.Json.JsonSerializer.Serialize((await h.Service.GetAsync("crm:9001", Phone, 9200)).Response);

        Assert.DoesNotContain("private@example.test", json);
        Assert.DoesNotContain("971500000900", json);
        Assert.DoesNotContain("Test Buyer", json);
    }

    // ---- missing project data ----

    [Fact]
    public async Task NoDetailsSource_KeepsCrmFields_AndReturnsEverythingElseAsNull()
    {
        var h = TwoUnitCustomer(); // details gateway answers NotAvailable for every unit

        var response = (await h.Service.GetAsync("crm:9001", Phone, 9200)).Response!;

        Assert.Equal("NotAvailable", response.DetailsStatus);
        Assert.Equal(79, response.Project!.ProjectId);
        Assert.Equal("Tiger Tower", response.Project.Name);
        Assert.Null(response.Project.Address);
        Assert.Null(response.Project.Status);
        Assert.Null(response.Project.ExpectedHandoverDate);
        Assert.Null(response.Project.ActualHandoverDate);
        Assert.Null(response.Project.Description);
        Assert.Null(response.Project.Amenities);
        Assert.Null(response.Unit!.Tower);
        Assert.Null(response.Unit.Bedrooms);
        Assert.Null(response.Unit.Area);
        Assert.Null(response.Unit.Parking);
        Assert.Null(response.Unit.ExpectedHandoverDate);
        Assert.Null(response.Unit.ActualHandoverDate);
        Assert.Null(response.Unit.UnitType.Name);
        Assert.Equal(2, response.Unit.UnitType.Code);
        Assert.Null(response.HandoverDateSource);
    }

    [Fact]
    public async Task UnitFoundButProjectBlock_Missing_LeavesProjectFactsNull()
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, CrmUnitDetailsResult.Found(new CrmUnitDetails(
            "Apartment", "Tower A", 2, 1250.5m, "sqft", null, null, null, Project: null)));

        var response = (await h.Service.GetAsync("crm:9001", Phone, 9200)).Response!;

        Assert.Equal("Available", response.DetailsStatus);
        Assert.Equal("Tower A", response.Unit!.Tower);
        Assert.Equal(2, response.Unit.Bedrooms);
        Assert.Equal(1250.5m, response.Unit.Area!.Value);
        Assert.Equal("sqft", response.Unit.Area.Unit);
        Assert.Null(response.Unit.Parking);
        Assert.Equal("Tiger Tower", response.Project!.Name);
        Assert.Null(response.Project.Address);
        Assert.Null(response.Project.Status);
        Assert.Null(response.HandoverDateSource);
    }

    [Fact]
    public async Task AreaWithoutAUnit_IsReturnedWithoutAGuessedUnit()
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, CrmUnitDetailsResult.Found(new CrmUnitDetails(null, null, null, 90m, null, null, null, null, null)));

        var area = (await h.Service.GetAsync("crm:9001", Phone, 9200)).Response!.Unit!.Area!;

        Assert.Equal(90m, area.Value);
        Assert.Null(area.Unit);
    }

    [Fact]
    public async Task ParkingKnownToBeNone_IsAnEmptyList_UnknownIsNull()
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, CrmUnitDetailsResult.Found(new CrmUnitDetails(null, null, null, null, null, [], null, null, null)));
        h.Details.Returns(9201, CrmUnitDetailsResult.Found(new CrmUnitDetails(
            null, null, null, null, null, [new CrmParkingSpace("P-114", "P1", "Covered")], null, null, null)));

        Assert.Empty((await h.Service.GetAsync("crm:9001", Phone, 9200)).Response!.Unit!.Parking!);
        var bay = Assert.Single((await h.Service.GetAsync("crm:9001", Phone, 9201)).Response!.Unit!.Parking!);
        Assert.Equal(("P-114", "P1", "Covered"), (bay.Number, bay.Level, bay.Type));
    }

    [Fact]
    public async Task DetailsSourceDown_StillAnswersWithCrmFields_AndSaysWhyValuesAreNull()
    {
        var h = TwoUnitCustomer();
        h.Details.Throws = new CrmGatewayUnavailableException("down");

        var response = (await h.Service.GetAsync("crm:9001", Phone, 9200)).Response!;

        Assert.Equal("Unavailable", response.DetailsStatus);
        Assert.Equal("Tiger Tower", response.Project!.Name);
        Assert.Null(response.Project.ExpectedHandoverDate);
    }

    // ---- handover-date mapping ----

    private static CrmUnitDetails WithHandover(
        DateOnly? unitExpected, DateOnly? unitActual, DateOnly? projectExpected, DateOnly? projectActual) =>
        new(null, null, null, null, null, null, unitExpected, unitActual,
            new CrmProjectDetails(null, "Under construction", projectExpected, projectActual, null, null));

    [Fact]
    public async Task ProjectLevelDatesOnly_AreReturnedAsProjectDates_AndSourceIsProject()
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, CrmUnitDetailsResult.Found(
            WithHandover(null, null, new DateOnly(2027, 3, 31), null)));

        var response = (await h.Service.GetAsync("crm:9001", Phone, 9200)).Response!;

        Assert.Equal("2027-03-31", response.Project!.ExpectedHandoverDate);
        Assert.Null(response.Project.ActualHandoverDate);
        Assert.Null(response.Unit!.ExpectedHandoverDate);
        Assert.Null(response.Unit.ActualHandoverDate);
        Assert.Equal("Project", response.HandoverDateSource);
    }

    [Fact]
    public async Task UnitLevelDates_AreKeptSeparateFromTheProjectDates_AndSourceIsUnit()
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, CrmUnitDetailsResult.Found(
            WithHandover(new DateOnly(2027, 6, 30), null, new DateOnly(2027, 3, 31), null)));

        var response = (await h.Service.GetAsync("crm:9001", Phone, 9200)).Response!;

        Assert.Equal("2027-06-30", response.Unit!.ExpectedHandoverDate);
        Assert.Equal("2027-03-31", response.Project!.ExpectedHandoverDate);
        Assert.Equal("Unit", response.HandoverDateSource);
    }

    [Fact]
    public async Task ExpectedAndActualAreNeverMixedUp_OrBackfilledFromEachOther()
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, CrmUnitDetailsResult.Found(
            WithHandover(new DateOnly(2026, 12, 1), new DateOnly(2026, 11, 20), new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 15))));
        h.Details.Returns(9201, CrmUnitDetailsResult.Found(
            WithHandover(null, null, null, new DateOnly(2026, 9, 15))));

        var both = (await h.Service.GetAsync("crm:9001", Phone, 9200)).Response!;
        Assert.Equal(("2026-12-01", "2026-11-20"), (both.Unit!.ExpectedHandoverDate, both.Unit.ActualHandoverDate));
        Assert.Equal(("2026-09-01", "2026-09-15"), (both.Project!.ExpectedHandoverDate, both.Project.ActualHandoverDate));

        // An actual date with no expected date stays an actual date; nothing fills the gap.
        var actualOnly = (await h.Service.GetAsync("crm:9001", Phone, 9201)).Response!;
        Assert.Null(actualOnly.Project!.ExpectedHandoverDate);
        Assert.Equal("2026-09-15", actualOnly.Project.ActualHandoverDate);
        Assert.Equal("Project", actualOnly.HandoverDateSource);
    }

    [Fact]
    public async Task NoHandoverDatesAnywhere_AreAllNull_AndSourceIsNull()
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, CrmUnitDetailsResult.Found(WithHandover(null, null, null, null)));

        var response = (await h.Service.GetAsync("crm:9001", Phone, 9200)).Response!;

        Assert.Null(response.Unit!.ExpectedHandoverDate);
        Assert.Null(response.Unit.ActualHandoverDate);
        Assert.Null(response.Project!.ExpectedHandoverDate);
        Assert.Null(response.Project.ActualHandoverDate);
        Assert.Null(response.HandoverDateSource);
    }
}

internal static class HarnessExtensions
{
    public static T With<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}

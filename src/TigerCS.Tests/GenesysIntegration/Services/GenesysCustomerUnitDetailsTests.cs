using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.CrmDocuments;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.GenesysIntegration;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Domain.Modules.CustomerVerification;
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
        public FakeVerificationSessionRepository Sessions { get; } = new();
        public FakeUnitReferenceRepository Units { get; } = new();
        public FakeContactReferenceRepository Contacts { get; } = new();
        public Guid Caller { get; } = Guid.NewGuid();
        public DateTime Now { get; set; } = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

        public GenesysCustomerUnitDetailsAppService Service =>
            new(Options, new CrmBuyerLookupAppService(Crm, NullLogger<CrmBuyerLookupAppService>.Instance), Details,
                new CrmDocumentOptions(), Sessions, Units, new FixedTime(() => Now),
                NullLogger<GenesysCustomerUnitDetailsAppService>.Instance);

        /// <summary>Records a session the way VerificationSessionAppService does (confirmed, 30 minutes) for a CRM unit.</summary>
        public Guid Session(
            string crmUnitId, VerificationMethod method = VerificationMethod.Otp, Guid? owner = null, bool confirm = true, TimeSpan? lifetime = null)
        {
            var unit = Units.Seed(crmUnitId, "1204");
            var contact = Contacts.Seed(unit.UnitReferenceId, "c-1", "Test Buyer");
            var id = Guid.NewGuid();
            var session = new VerificationSession(
                id, owner ?? Caller, unit.UnitReferenceId, contact.ContactReferenceId, "1204", null, null, null, null, null,
                Now, Now.Add(lifetime ?? TimeSpan.FromMinutes(30)), null);
            if (confirm)
            {
                session.Confirm(Now, method);
            }

            Sessions.AddAsync(session).GetAwaiter().GetResult();
            return id;
        }

        public Task<GenesysUnitDetailsResult> Get(int unitId, Guid? sessionId = null, Guid? caller = null) =>
            Service.GetAsync("crm:9001", Phone, unitId, caller ?? Caller, sessionId);
    }

    private sealed class FixedTime(Func<DateTime> now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now(), TimeSpan.Zero);
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

    // ---- project completion (distinct from handover) ----

    [Fact]
    public async Task Completion_IsMappedAndKeptDistinctFromHandover()
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, CrmUnitDetailsResult.Found(new CrmUnitDetails(
            null, null, null, null, null, null, null, null,
            new CrmProjectDetails(null, "Under construction", new DateOnly(2027, 3, 31), null, null, null,
                CompletionPercentage: 62.5m, ExpectedCompletionDate: new DateOnly(2027, 1, 31), ActualCompletionDate: null))));

        var project = (await h.Get(9200)).Response!.Project!;

        Assert.Equal(62.5m, project.CompletionPercentage);
        Assert.Equal("2027-01-31", project.ExpectedCompletionDate);
        Assert.Null(project.ActualCompletionDate);
        Assert.Equal("2027-03-31", project.ExpectedHandoverDate);
        Assert.Null(project.ActualHandoverDate);
    }

    [Fact]
    public async Task ActualCompletionWithoutHandover_DoesNotInventAHandoverDate()
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, CrmUnitDetailsResult.Found(new CrmUnitDetails(
            null, null, null, null, null, null, null, null,
            new CrmProjectDetails(null, "Completed", null, null, null, null, 100m, null, new DateOnly(2026, 9, 1)))));

        var response = (await h.Get(9200)).Response!;

        Assert.Equal(100m, response.Project!.CompletionPercentage);
        Assert.Equal("2026-09-01", response.Project.ActualCompletionDate);
        Assert.Null(response.Project.ExpectedHandoverDate);
        Assert.Null(response.Project.ActualHandoverDate);
        Assert.Null(response.HandoverDateSource);
    }

    [Fact]
    public async Task ZeroPercentComplete_IsAGenuineZero_NotNull()
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, CrmUnitDetailsResult.Found(new CrmUnitDetails(
            null, null, null, null, null, null, null, null,
            new CrmProjectDetails(null, "Launched", null, null, null, null, 0m))));

        Assert.Equal(0m, (await h.Get(9200)).Response!.Project!.CompletionPercentage);
    }

    [Fact]
    public async Task CompletionNotRecorded_IsNull()
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, CrmUnitDetailsResult.Found(WithHandover(null, null, null, null)));

        var project = (await h.Get(9200)).Response!.Project!;

        Assert.Null(project.CompletionPercentage);
        Assert.Null(project.ExpectedCompletionDate);
        Assert.Null(project.ActualCompletionDate);
    }

    // ---- sale: sold price and registration cost ----

    private static CrmUnitDetailsResult WithSale(int? leadId, decimal? price, decimal? fee, string? currency = "AED") =>
        CrmUnitDetailsResult.Found(new CrmUnitDetails(
            null, null, null, null, null, null, null, null, null, new CrmSaleDetails(leadId, price, fee, currency)));

    [Fact]
    public async Task ValidProof_ReturnsTheRecordedSale_AskingCrmForThatCustomersLead()
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, WithSale(9100, 1850000.50m, 74000m));

        var response = (await h.Get(9200, h.Session("9200"))).Response!;

        Assert.Equal("Available", response.FinancialDetailsStatus);
        Assert.Equal(1850000.50m, response.Sale!.SoldPrice!.Amount);
        Assert.Equal("AED", response.Sale.SoldPrice.Currency);
        Assert.Equal(74000m, response.Sale.RegistrationCost!.Amount);
        Assert.Equal("AED", response.Sale.RegistrationCost.Currency);
        Assert.Equal((9100, true), Assert.Single(h.Details.SaleRequests));
    }

    [Fact]
    public async Task NoProof_WithholdsTheSale_AndNeverAsksCrmForIt()
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, WithSale(9100, 1850000m, 74000m));

        var response = (await h.Get(9200)).Response!;

        Assert.Equal("VerificationRequired", response.FinancialDetailsStatus);
        Assert.Null(response.Sale);
        Assert.Equal((9100, false), Assert.Single(h.Details.SaleRequests));
        // The non-financial details still come back.
        Assert.Equal("Available", response.DetailsStatus);
    }

    [Fact]
    public async Task PhoneAndCustomerIdAlone_AreNotProofOfIdentity_ForFinancialData()
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, WithSale(9100, 1850000m, 74000m));

        // Passes the ownership check (correct customer, own unit) yet carries no server-recorded proof.
        var response = (await h.Service.GetAsync("crm:9001", Phone, 9200)).Response!;

        Assert.Null(response.Sale);
        Assert.False(h.Details.SaleRequests.Single().IncludeSale);
    }

    public enum BadProof { Unknown, OtherAgent, Unconfirmed, Expired, WeakMethod, OtherUnit }

    [Theory]
    [InlineData(BadProof.Unknown)]
    [InlineData(BadProof.OtherAgent)]
    [InlineData(BadProof.Unconfirmed)]
    [InlineData(BadProof.Expired)]
    [InlineData(BadProof.WeakMethod)]
    [InlineData(BadProof.OtherUnit)]
    public async Task InvalidOrExpiredProof_WithholdsTheSale_WithOneAnswer(BadProof kind)
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, WithSale(9100, 1850000m, 74000m));
        var session = kind switch
        {
            BadProof.Unknown => Guid.NewGuid(),
            BadProof.OtherAgent => h.Session("9200", owner: Guid.NewGuid()),
            BadProof.Unconfirmed => h.Session("9200", confirm: false),
            BadProof.Expired => h.Session("9200").With(_ => h.Now = h.Now.AddHours(1)),
            BadProof.WeakMethod => h.Session("9200", VerificationMethod.ManualAgentConfirmation),
            _ => h.Session("9201")
        };

        var response = (await h.Get(9200, session)).Response!;

        Assert.Equal("VerificationFailed", response.FinancialDetailsStatus);
        Assert.Null(response.Sale);
        Assert.False(h.Details.SaleRequests.Single().IncludeSale);
        Assert.Equal("Tiger Tower", response.Project!.Name);
    }

    [Fact]
    public async Task ProofThatExpiresBetweenCalls_StopsReleasingTheSale()
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, WithSale(9100, 1850000m, 74000m));
        var session = h.Session("9200");

        Assert.Equal("Available", (await h.Get(9200, session)).Response!.FinancialDetailsStatus);

        h.Now = h.Now.AddMinutes(31);
        Assert.Equal("VerificationFailed", (await h.Get(9200, session)).Response!.FinancialDetailsStatus);
    }

    [Fact]
    public async Task ProofForAnotherCustomersUnit_DoesNotOpenUpTheUnit()
    {
        var h = TwoUnitCustomer();
        var session = h.Session("9999");

        var result = await h.Get(9999, session);

        Assert.Equal(GenesysUnitDetailsOutcome.UnitNotEligible, result.Outcome);
        Assert.Null(result.Response);
        Assert.Empty(h.Details.Calls);
    }

    [Fact]
    public async Task SaleOfADifferentLead_IsDiscarded()
    {
        var h = TwoUnitCustomer();
        // CRM answers with an unrelated historical booking (lead 5555), not the Lead 9100 bound to unit 9200.
        h.Details.Returns(9200, WithSale(5555, 999999m, 1m));

        var response = (await h.Get(9200, h.Session("9200"))).Response!;

        Assert.Equal("NotAvailable", response.FinancialDetailsStatus);
        Assert.Null(response.Sale);
    }

    [Fact]
    public async Task SaleWithNoLeadEcho_IsDiscarded()
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, WithSale(null, 1850000m, 74000m));

        var response = (await h.Get(9200, h.Session("9200"))).Response!;

        Assert.Equal("NotAvailable", response.FinancialDetailsStatus);
        Assert.Null(response.Sale);
    }

    [Fact]
    public async Task EachUnitGetsItsOwnSale()
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, WithSale(9100, 1000000m, 40000m));
        h.Details.Returns(9201, WithSale(9101, 2000000m, 80000m));

        var first = (await h.Get(9200, h.Session("9200"))).Response!.Sale!;
        var second = (await h.Get(9201, h.Session("9201"))).Response!.Sale!;

        Assert.Equal((1000000m, 40000m), (first.SoldPrice!.Amount, first.RegistrationCost!.Amount));
        Assert.Equal((2000000m, 80000m), (second.SoldPrice!.Amount, second.RegistrationCost!.Amount));
        Assert.Equal([(9100, true), (9101, true)], h.Details.SaleRequests);
    }

    [Fact]
    public async Task GenuineZeroPriceAndFee_AreKept_NotTurnedIntoNull()
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, WithSale(9100, 0m, 0m));

        var sale = (await h.Get(9200, h.Session("9200"))).Response!.Sale!;

        Assert.Equal(0m, sale.SoldPrice!.Amount);
        Assert.Equal(0m, sale.RegistrationCost!.Amount);
    }

    [Fact]
    public async Task MissingRegistrationCost_OrSoldPrice_IsNull_NeverCalculated()
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, WithSale(9100, 1850000m, null));
        h.Details.Returns(9201, WithSale(9101, null, 74000m));

        var noFee = (await h.Get(9200, h.Session("9200"))).Response!;
        Assert.Equal("Available", noFee.FinancialDetailsStatus);
        Assert.Equal(1850000m, noFee.Sale!.SoldPrice!.Amount);
        Assert.Null(noFee.Sale.RegistrationCost);

        var noPrice = (await h.Get(9201, h.Session("9201"))).Response!;
        Assert.Null(noPrice.Sale!.SoldPrice);
        Assert.Equal(74000m, noPrice.Sale.RegistrationCost!.Amount);
    }

    [Fact]
    public async Task CurrencyCrmDoesNotState_IsNull_NotAssumed()
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, WithSale(9100, 1850000m, 74000m, currency: null));

        var sale = (await h.Get(9200, h.Session("9200"))).Response!.Sale!;

        Assert.Null(sale.SoldPrice!.Currency);
        Assert.Null(sale.RegistrationCost!.Currency);
    }

    [Fact]
    public async Task CrmWithoutSaleData_IsNotAvailable_AndOtherDetailsRemain()
    {
        var h = TwoUnitCustomer();

        var response = (await h.Get(9200, h.Session("9200"))).Response!;

        Assert.Equal("NotAvailable", response.FinancialDetailsStatus);
        Assert.Null(response.Sale);
        Assert.Equal("Tiger Tower", response.Project!.Name);
        Assert.Equal("9100", response.Unit!.Booking.Reference);
    }

    [Fact]
    public async Task CrmDownDuringSale_IsUnavailable_WithPartialRealData()
    {
        var h = TwoUnitCustomer();
        h.Details.Throws = new CrmGatewayUnavailableException("down");

        var response = (await h.Get(9200, h.Session("9200"))).Response!;

        Assert.Equal("Unavailable", response.FinancialDetailsStatus);
        Assert.Equal("Unavailable", response.DetailsStatus);
        Assert.Null(response.Sale);
        Assert.Equal("Tiger Tower", response.Project!.Name);
    }

    [Fact]
    public async Task UnitSelection_NeverTouchesTheSale()
    {
        var h = TwoUnitCustomer();

        var response = (await h.Service.GetAsync("crm:9001", Phone, null, h.Caller, h.Session("9200"))).Response!;

        Assert.Null(response.Sale);
        Assert.Null(response.FinancialDetailsStatus);
        Assert.Empty(h.Details.Calls);
    }

    [Fact]
    public async Task SaleNeverAppearsInTheJson_WithoutProof()
    {
        var h = TwoUnitCustomer();
        h.Details.Returns(9200, WithSale(9100, 1850000m, 74000m));

        var json = System.Text.Json.JsonSerializer.Serialize((await h.Get(9200)).Response);

        Assert.DoesNotContain("1850000", json);
        Assert.DoesNotContain("74000", json);
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

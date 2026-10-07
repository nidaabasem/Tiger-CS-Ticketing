using TigerCS.Application.Modules.CustomerVerification.CustomerLookup;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// Business-rule change: which of CRM/PACT/Tasleeh are searched depends on
/// the IntakeRecord's DepartmentId — the Department's configured source(s)
/// only when set (never a hardcoded department-id branch; see
/// <see cref="DepartmentCustomerLookupSource"/>), or all three when no
/// Department was selected. Either way, the search is enrichment/
/// identification only, never a Ticket creation gate. Required regression
/// coverage: department scoped to one source, department scoped to
/// multiple sources, no department (all three), each source's Found/
/// NotFound/Failed outcome, and that a partial result set (one source
/// Found, one Failed, one NotFound) still returns everything together.
/// </summary>
public class CustomerLookupAppServiceTests
{
    private const string Phone = "+971500000001";
    private const int DepartmentId = 7;

    private sealed record Fixture(
        CustomerLookupAppService Service,
        FakeIntakeRecordRepository IntakeRecords,
        FakeDepartmentCustomerLookupSourceRepository DepartmentSources,
        FakeCrmCustomerLookupGateway Crm,
        FakePactCustomerLookupGateway Pact,
        FakeTasleehGateway Tasleeh,
        FakeCrmGateway CrmUnitGateway);

    private static Fixture CreateService(TimeProvider? timeProvider = null)
    {
        var intakeRecords = new FakeIntakeRecordRepository();
        var departmentSources = new FakeDepartmentCustomerLookupSourceRepository();
        var crmLookup = new FakeCrmCustomerLookupGateway();
        var pact = new FakePactCustomerLookupGateway();
        var tasleeh = new FakeTasleehGateway();

        // CustomerLookupAppService resolves a CRM phone match's local
        // reference ids by re-fetching the same unit/contacts through
        // CrmUnitLookupAppService (the existing cache-aside upsert) — in
        // production both interfaces are backed by the same MockCrmGateway
        // fixture data, so a test that seeds a phone match must seed this
        // gateway with the matching unit/contacts too, exactly as
        // MockCrmGateway's single Fixtures dictionary does for real.
        var crmGateway = new FakeCrmGateway();
        var unitReferences = new FakeUnitReferenceRepository();
        var contactReferences = new FakeContactReferenceRepository();
        var crmUnitOfWork = new FakeCustomerVerificationUnitOfWork();
        var crmUnitLookup = new CrmUnitLookupAppService(crmGateway, unitReferences, contactReferences, crmUnitOfWork, TimeProvider.System);

        var service = new CustomerLookupAppService(intakeRecords, departmentSources, crmLookup, pact, tasleeh, crmUnitLookup, timeProvider ?? TimeProvider.System);

        return new Fixture(service, intakeRecords, departmentSources, crmLookup, pact, tasleeh, crmGateway);
    }

    private static async Task<long> SeedIntakeAsync(
        FakeIntakeRecordRepository repo, string phoneNumber = Phone, int? departmentId = null)
    {
        var record = new TigerCS.Domain.Modules.Ticketing.IntakeRecord(
            WellKnownChannels.Phone, phoneNumber, departmentId, isUnitRelated: false, rawUnitNumberEntered: null, priorityHint: null, Guid.NewGuid(), DateTime.UtcNow);
        await repo.AddAsync(record);
        return record.IntakeRecordId;
    }

    [Fact]
    public async Task SearchAsync_IntakeRecordNotFound_ReturnsNotFound()
    {
        var f = CreateService();

        var result = await f.Service.SearchAsync(999);

        Assert.Equal(CustomerLookupOutcome.IntakeRecordNotFound, result.Outcome);
    }

    [Fact]
    public async Task SearchAsync_NoDepartment_SearchesAllThreeSourcesFindNothing_ReturnsNotFoundForEach()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords, departmentId: null);

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        Assert.Equal(3, result.Response!.Sources.Count);
        Assert.All(result.Response.Sources, s => Assert.Equal("NotFound", s.Status));
    }

    [Fact]
    public async Task SearchAsync_CrmFindsMatch_ReturnsFoundWithLocalUnitAndContactReferenceIds()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        var unit = new TigerCS.Application.Modules.CustomerVerification.CrmIntegration.CrmUnitResult("CRM-UNIT-1001", "1204", "Tiger Tower A", "Tower A", "Residential");
        var contact = new TigerCS.Application.Modules.CustomerVerification.CrmIntegration.CrmContactResult(
            "CRM-CONTACT-2001", "Ahmed Al-Farsi", Phone, ContactType.Owner, null);
        f.CrmUnitGateway.Seed(unit, contact);
        f.Crm.Seed(Phone, new CrmCustomerMatch(
            "CRM-CUST-9001", contact.DisplayName, Phone, "ahmed@example.com", "Buyer",
            [new CrmCustomerUnitMatch(unit.CrmUnitId, contact.CrmContactId)]));

        var result = await f.Service.SearchAsync(intakeRecordId);

        var crmResult = Assert.Single(result.Response!.Sources, s => s.Source == "Crm");
        Assert.Equal("Found", crmResult.Status);
        var customer = Assert.Single(crmResult.Customers);
        Assert.Equal("CRM-CUST-9001", customer.ExternalCustomerId);
        Assert.Equal("Ahmed Al-Farsi", customer.DisplayName);
        Assert.Equal("ahmed@example.com", customer.Email);
        Assert.Equal("Buyer", customer.CustomerType);
        var matchedUnit = Assert.Single(customer.Units);
        Assert.NotNull(matchedUnit.UnitReferenceId);
        Assert.NotNull(matchedUnit.ContactReferenceId);
        Assert.Equal("1204", matchedUnit.UnitNumber);
    }

    [Fact]
    public async Task SearchAsync_CrmMatchHasMultipleUnits_ReturnsAllOfThemForTheSameCustomer()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        var unit1 = new TigerCS.Application.Modules.CustomerVerification.CrmIntegration.CrmUnitResult("CRM-UNIT-1001", "1204", "Tiger Tower A", "Tower A", "Residential");
        var contact1 = new TigerCS.Application.Modules.CustomerVerification.CrmIntegration.CrmContactResult(
            "CRM-CONTACT-2001", "Ahmed Al-Farsi", Phone, ContactType.Owner, null);
        var unit2 = new TigerCS.Application.Modules.CustomerVerification.CrmIntegration.CrmUnitResult("CRM-UNIT-1002", "0507", "Tiger Tower B", "Tower B", "Commercial");
        var contact2 = new TigerCS.Application.Modules.CustomerVerification.CrmIntegration.CrmContactResult(
            "CRM-CONTACT-2002", "Ahmed Al-Farsi", Phone, ContactType.Owner, null);
        f.CrmUnitGateway.Seed(unit1, contact1);
        f.CrmUnitGateway.Seed(unit2, contact2);
        f.Crm.Seed(Phone, new CrmCustomerMatch(
            "CRM-CUST-9001", "Ahmed Al-Farsi", Phone, null, "Buyer",
            [new CrmCustomerUnitMatch(unit1.CrmUnitId, contact1.CrmContactId), new CrmCustomerUnitMatch(unit2.CrmUnitId, contact2.CrmContactId)]));

        var result = await f.Service.SearchAsync(intakeRecordId);

        var crmResult = Assert.Single(result.Response!.Sources, s => s.Source == "Crm");
        var customer = Assert.Single(crmResult.Customers);
        Assert.Equal(2, customer.Units.Count);
        Assert.Contains(customer.Units, u => u.UnitNumber == "1204");
        Assert.Contains(customer.Units, u => u.UnitNumber == "0507");
    }

    [Fact]
    public async Task SearchAsync_CrmReturnsMultipleCustomers_AllAreReturned()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        var unit1 = new TigerCS.Application.Modules.CustomerVerification.CrmIntegration.CrmUnitResult("CRM-UNIT-1001", "1204", "Tiger Tower A", "Tower A", "Residential");
        var contact1 = new TigerCS.Application.Modules.CustomerVerification.CrmIntegration.CrmContactResult(
            "CRM-CONTACT-2001", "Ahmed Al-Farsi", Phone, ContactType.Owner, null);
        var unit2 = new TigerCS.Application.Modules.CustomerVerification.CrmIntegration.CrmUnitResult("CRM-UNIT-1002", "0507", "Tiger Tower B", "Tower B", "Commercial");
        var contact2 = new TigerCS.Application.Modules.CustomerVerification.CrmIntegration.CrmContactResult(
            "CRM-CONTACT-2002", "Ahmad Al-Farsi Jr.", Phone, ContactType.Owner, null);
        f.CrmUnitGateway.Seed(unit1, contact1);
        f.CrmUnitGateway.Seed(unit2, contact2);
        f.Crm.Seed(Phone, new CrmCustomerMatch("CRM-CUST-9001", "Ahmed Al-Farsi", Phone, null, "Buyer", [new CrmCustomerUnitMatch(unit1.CrmUnitId, contact1.CrmContactId)]));
        f.Crm.Seed(Phone, new CrmCustomerMatch("CRM-CUST-9002", "Ahmad Al-Farsi Jr.", Phone, null, "Buyer", [new CrmCustomerUnitMatch(unit2.CrmUnitId, contact2.CrmContactId)]));

        var result = await f.Service.SearchAsync(intakeRecordId);

        var crmResult = Assert.Single(result.Response!.Sources, s => s.Source == "Crm");
        Assert.Equal("Found", crmResult.Status);
        Assert.Equal(2, crmResult.Customers.Count);
        Assert.Equal("1204", Assert.Single(crmResult.Customers, c => c.ExternalCustomerId == "CRM-CUST-9001").Units.Single().UnitNumber);
        Assert.Equal("0507", Assert.Single(crmResult.Customers, c => c.ExternalCustomerId == "CRM-CUST-9002").Units.Single().UnitNumber);
    }

    [Fact]
    public async Task SearchAsync_CrmCustomerHasNoUnits_StillFoundWithEmptyUnitsList()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Crm.Seed(Phone, new CrmCustomerMatch("CRM-CUST-9003", "Khalid Nasser", Phone, null, "Buyer", []));

        var result = await f.Service.SearchAsync(intakeRecordId);

        var crmResult = Assert.Single(result.Response!.Sources, s => s.Source == "Crm");
        Assert.Equal("Found", crmResult.Status);
        var customer = Assert.Single(crmResult.Customers);
        Assert.Empty(customer.Units);
    }

    [Fact]
    public async Task SearchAsync_CrmMatchHasDuplicateUnitRows_DoesNotDuplicateUnitsInResult()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        var unit = new TigerCS.Application.Modules.CustomerVerification.CrmIntegration.CrmUnitResult("CRM-UNIT-1001", "1204", "Tiger Tower A", "Tower A", "Residential");
        var contact = new TigerCS.Application.Modules.CustomerVerification.CrmIntegration.CrmContactResult(
            "CRM-CONTACT-2001", "Ahmed Al-Farsi", Phone, ContactType.Owner, null);
        f.CrmUnitGateway.Seed(unit, contact);
        // A duplicate relationship row for the very same unit/contact pair —
        // must never surface as two units for the customer.
        f.Crm.Seed(Phone, new CrmCustomerMatch(
            "CRM-CUST-9001", "Ahmed Al-Farsi", Phone, null, "Buyer",
            [new CrmCustomerUnitMatch(unit.CrmUnitId, contact.CrmContactId), new CrmCustomerUnitMatch(unit.CrmUnitId, contact.CrmContactId)]));

        var result = await f.Service.SearchAsync(intakeRecordId);

        var customer = Assert.Single(Assert.Single(result.Response!.Sources, s => s.Source == "Crm").Customers);
        Assert.Single(customer.Units);
    }

    [Fact]
    public async Task SearchAsync_CrmUnavailable_ReturnsFailed_NeverBlocksTheOtherTwoSources()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Crm.ThrowUnavailable = true;
        f.Pact.Seed(Phone, new PactCustomerMatchDto("PACT-CUST-1", "Fatima Noor", Phone, Email: null, CustomerType: null, Contracts: []));

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        Assert.Equal("Failed", Assert.Single(result.Response!.Sources, s => s.Source == "Crm").Status);
        Assert.Equal("Found", Assert.Single(result.Response.Sources, s => s.Source == "Pact").Status);
    }

    [Fact]
    public async Task SearchAsync_PactFindsMatch_ReturnsFound()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Pact.Seed(Phone, new PactCustomerMatchDto("PACT-CUST-1", "Fatima Noor", Phone, Email: null, CustomerType: null, Contracts: []));

        var result = await f.Service.SearchAsync(intakeRecordId);

        var pactResult = Assert.Single(result.Response!.Sources, s => s.Source == "Pact");
        Assert.Equal("Found", pactResult.Status);
        var customer = Assert.Single(pactResult.Customers);
        Assert.Equal("Fatima Noor", customer.DisplayName);
        Assert.Empty(customer.Units);
    }

    [Fact]
    public async Task SearchAsync_PactFindsMultipleMatches_ReturnsFoundWithAllCustomers()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Pact.Seed(Phone, new PactCustomerMatchDto("PACT-CUST-1", "Fatima Noor", Phone, Email: null, CustomerType: null, Contracts: []));
        f.Pact.Seed(Phone, new PactCustomerMatchDto("PACT-CUST-2", "Youssef Noor", Phone, Email: null, CustomerType: null, Contracts: []));

        var result = await f.Service.SearchAsync(intakeRecordId);

        var pactResult = Assert.Single(result.Response!.Sources, s => s.Source == "Pact");
        Assert.Equal("Found", pactResult.Status);
        Assert.Equal(2, pactResult.Customers.Count);
        Assert.All(pactResult.Customers, c => Assert.Empty(c.Units));
    }

    [Fact]
    public async Task SearchAsync_PactMatchWithContracts_ReturnsAllUnitsWithoutLocalReferenceIds()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Pact.Seed(Phone, new PactCustomerMatchDto(
            "PACT-CUST-1", "Fatima Noor", Phone, "fatima@example.com", "Tenant",
            [
                new PactContractDto("PACT-UNIT-A-0304", "PACT-CNT-88001", "0304", "Tiger Marina Residences", "Residential"),
                new PactContractDto("PACT-UNIT-B-1105", "PACT-CNT-88002", "1105", "Tiger Bay Towers", "Commercial")
            ]));

        var result = await f.Service.SearchAsync(intakeRecordId);

        var pactResult = Assert.Single(result.Response!.Sources, s => s.Source == "Pact");
        Assert.Equal("Found", pactResult.Status);
        var customer = Assert.Single(pactResult.Customers);
        Assert.Equal("fatima@example.com", customer.Email);
        Assert.Equal("Tenant", customer.CustomerType);
        // All of PACT's contracts/units come back — never just the first one
        // (no automatic selection; only the CS agent chooses).
        Assert.Equal(2, customer.Units.Count);
        Assert.Contains(customer.Units, u => u.UnitNumber == "0304" && u.PropertyName == "Tiger Marina Residences");
        Assert.Contains(customer.Units, u => u.UnitNumber == "1105" && u.PropertyName == "Tiger Bay Towers");
        // PACT has no local UnitReference/ContactReference cache — display
        // enrichment only, never linkable to a Ticket by id.
        Assert.All(customer.Units, u =>
        {
            Assert.Null(u.UnitReferenceId);
            Assert.Null(u.ContactReferenceId);
        });
    }

    [Fact]
    public async Task SearchAsync_PactMatchHasDuplicateContractRows_DoesNotDuplicateUnitsInResult()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        var contract = new PactContractDto("PACT-UNIT-A-0304", "PACT-CNT-88001", "0304", "Tiger Marina Residences", "Residential");
        f.Pact.Seed(Phone, new PactCustomerMatchDto(
            "PACT-CUST-1", "Fatima Noor", Phone, Email: null, CustomerType: null, Contracts: [contract, contract]));

        var result = await f.Service.SearchAsync(intakeRecordId);

        var customer = Assert.Single(Assert.Single(result.Response!.Sources, s => s.Source == "Pact").Customers);
        Assert.Single(customer.Units);
    }

    // ---------------------------------------------------------------
    // PACT contract expiry: contractEndDate before today's DUBAI date flags
    // the contract; selection filtering happens in New Ticket. The clock below is
    // 2026-10-05 21:30 UTC — still 5 October on a UTC server, but already
    // 6 October 01:30 in Dubai — so "today" for every test here is
    // 6 October, and a contract ending on 5 October counts as expired
    // even though the server's own calendar still says it ends today.
    // ---------------------------------------------------------------

    private static readonly DateTime DubaiBoundaryUtc = new(2026, 10, 5, 21, 30, 0, DateTimeKind.Utc);
    private static readonly DateOnly DubaiToday = new(2026, 10, 6);

    [Fact]
    public async Task LookupRetainsDifferentCompaniesAndHistoricalContractsOnTheSameUnit()
    {
        var f = CreateService(new FakeTimeProvider(DubaiBoundaryUtc));
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        var expired = new PactContractDto("U-1", "OLD", "0304", "Tiger Marina", "Residential", 4, DubaiToday.AddDays(-1));
        var current = expired with { ContractNumber = "NEW", CompanyId = 25, ContractEndDate = DubaiToday };
        f.Pact.Seed(Phone, new PactCustomerMatchDto("TENANT", "Fatima Noor", Phone, null, "2", [expired, current, expired]));
        var customer = Assert.Single(PactSource(await f.Service.SearchAsync(intakeRecordId)).Customers);
        Assert.Equal(2, customer.Units.Count);
        Assert.Equal([25, 4], customer.Units.Select(u => u.CompanyId));
        Assert.False(customer.Units[0].IsContractExpired);
        Assert.True(customer.Units[1].IsContractExpired);
    }

    private static PactContractDto Contract(string unitId, DateOnly? endDate) =>
        new(unitId, $"CNT-{unitId}", unitId, "Tiger Marina Residences", "Residential", ContractEndDate: endDate);

    private static CustomerLookupSourceResultDto PactSource(CustomerLookupResult result) =>
        Assert.Single(result.Response!.Sources, s => s.Source == "Pact");

    [Fact]
    public async Task SearchAsync_PactContractEndedYesterdayInDubai_RetainsIdentityAndFlagsExpiry()
    {
        var f = CreateService(new FakeTimeProvider(DubaiBoundaryUtc));
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Pact.Seed(Phone, new PactCustomerMatchDto(
            "PACT-CUST-1", "Fatima Noor", Phone, Email: null, CustomerType: null,
            Contracts: [Contract("U-1", DubaiToday.AddDays(-1))]));

        var result = await f.Service.SearchAsync(intakeRecordId);

        var pactResult = PactSource(result);
        Assert.Equal("Found", pactResult.Status);
        Assert.True(Assert.Single(Assert.Single(pactResult.Customers).Units).IsContractExpired);
    }

    [Fact]
    public async Task SearchAsync_PactContractEndsTodayInDubai_StaysVisible()
    {
        var f = CreateService(new FakeTimeProvider(DubaiBoundaryUtc));
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Pact.Seed(Phone, new PactCustomerMatchDto(
            "PACT-CUST-1", "Fatima Noor", Phone, Email: null, CustomerType: null,
            Contracts: [Contract("U-1", DubaiToday)]));

        var result = await f.Service.SearchAsync(intakeRecordId);

        var pactResult = PactSource(result);
        Assert.Equal("Found", pactResult.Status);
        var unit = Assert.Single(Assert.Single(pactResult.Customers).Units);
        Assert.Equal("U-1", unit.ExternalUnitId);
    }

    [Fact]
    public async Task SearchAsync_PactContractEndsInFuture_StaysVisible()
    {
        var f = CreateService(new FakeTimeProvider(DubaiBoundaryUtc));
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Pact.Seed(Phone, new PactCustomerMatchDto(
            "PACT-CUST-1", "Fatima Noor", Phone, Email: null, CustomerType: null,
            Contracts: [Contract("U-1", DubaiToday.AddYears(1))]));

        var result = await f.Service.SearchAsync(intakeRecordId);

        var pactResult = PactSource(result);
        Assert.Equal("Found", pactResult.Status);
        Assert.Single(Assert.Single(pactResult.Customers).Units);
    }

    [Fact]
    public async Task SearchAsync_PactMixedActiveAndExpiredContracts_RetainsAllEvidenceAndFlagsExpiry()
    {
        var f = CreateService(new FakeTimeProvider(DubaiBoundaryUtc));
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Pact.Seed(Phone, new PactCustomerMatchDto(
            "PACT-CUST-1", "Fatima Noor", Phone, "fatima@example.com", "2",
            Contracts:
            [
                Contract("U-EXPIRED", DubaiToday.AddDays(-1)),
                Contract("U-TODAY", DubaiToday),
                Contract("U-FUTURE", DubaiToday.AddMonths(6)),
                Contract("U-LONG-GONE", new DateOnly(2020, 1, 31))
            ]));

        var result = await f.Service.SearchAsync(intakeRecordId);

        var pactResult = PactSource(result);
        Assert.Equal("Found", pactResult.Status);
        var customer = Assert.Single(pactResult.Customers);
        // Identity is untouched by the filter.
        Assert.Equal("PACT-CUST-1", customer.ExternalCustomerId);
        Assert.Equal("Fatima Noor", customer.DisplayName);
        Assert.Equal("fatima@example.com", customer.Email);
        Assert.Equal("2", customer.CustomerType);
        Assert.Equal(4, customer.Units.Count);
        Assert.Equal(["U-TODAY", "U-FUTURE"], customer.Units.Where(u => !u.IsContractExpired).Select(u => u.ExternalUnitId).ToArray());
        Assert.Equal(2, customer.Units.Count(u => u.IsContractExpired));
    }

    [Fact]
    public async Task SearchAsync_PactCustomerWithAllContractsExpired_RemainsAlongsideCurrentCustomers()
    {
        var f = CreateService(new FakeTimeProvider(DubaiBoundaryUtc));
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Pact.Seed(Phone, new PactCustomerMatchDto(
            "PACT-CUST-EXPIRED", "Former Tenant", Phone, Email: null, CustomerType: null,
            Contracts: [Contract("U-1", DubaiToday.AddDays(-1)), Contract("U-2", new DateOnly(2024, 12, 31))]));
        f.Pact.Seed(Phone, new PactCustomerMatchDto(
            "PACT-CUST-CURRENT", "Current Tenant", Phone, Email: null, CustomerType: null,
            Contracts: [Contract("U-3", DubaiToday.AddDays(30))]));

        var result = await f.Service.SearchAsync(intakeRecordId);

        var pactResult = PactSource(result);
        Assert.Equal("Found", pactResult.Status);
        Assert.Equal(2, pactResult.Customers.Count);
        var customer = Assert.Single(pactResult.Customers, c => c.ExternalCustomerId == "PACT-CUST-CURRENT");
        Assert.Equal("U-3", Assert.Single(customer.Units).ExternalUnitId);
        Assert.All(pactResult.Customers.Single(c => c.ExternalCustomerId == "PACT-CUST-EXPIRED").Units, u => Assert.True(u.IsContractExpired));
    }

    [Fact]
    public async Task SearchAsync_PactEveryCustomerExpired_RetainsBothTenantsForHistoricalReads()
    {
        var f = CreateService(new FakeTimeProvider(DubaiBoundaryUtc));
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Pact.Seed(Phone, new PactCustomerMatchDto(
            "PACT-CUST-1", "Former Tenant", Phone, Email: null, CustomerType: null,
            Contracts: [Contract("U-1", DubaiToday.AddDays(-1))]));
        f.Pact.Seed(Phone, new PactCustomerMatchDto(
            "PACT-CUST-2", "Other Former Tenant", Phone, Email: null, CustomerType: null,
            Contracts: [Contract("U-2", DubaiToday.AddDays(-400))]));

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        Assert.Equal("Found", PactSource(result).Status);
        Assert.Equal(2, PactSource(result).Customers.Count);
        Assert.All(PactSource(result).Customers.SelectMany(c => c.Units), u => Assert.True(u.IsContractExpired));
    }

    [Fact]
    public async Task SearchAsync_PactContractWithMissingOrUnreadableEndDate_StaysVisible()
    {
        var f = CreateService(new FakeTimeProvider(DubaiBoundaryUtc));
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        // The gateway maps an absent or malformed contractEndDate to null —
        // the filter must treat that as current, never as expired.
        f.Pact.Seed(Phone, new PactCustomerMatchDto(
            "PACT-CUST-1", "Fatima Noor", Phone, Email: null, CustomerType: null,
            Contracts: [Contract("U-NO-DATE", endDate: null), Contract("U-EXPIRED", DubaiToday.AddDays(-1))]));

        var result = await f.Service.SearchAsync(intakeRecordId);

        var pactResult = PactSource(result);
        Assert.Equal("Found", pactResult.Status);
        var units = Assert.Single(pactResult.Customers).Units;
        Assert.Equal(2, units.Count);
        var unit = Assert.Single(units, u => !u.IsContractExpired);
        Assert.Equal("U-NO-DATE", unit.ExternalUnitId);
    }

    [Fact]
    public async Task SearchAsync_PactCustomerWithNoContractsAtAll_IsStillShown()
    {
        var f = CreateService(new FakeTimeProvider(DubaiBoundaryUtc));
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Pact.Seed(Phone, new PactCustomerMatchDto("PACT-CUST-1", "Fatima Noor", Phone, Email: null, CustomerType: null, Contracts: []));

        var result = await f.Service.SearchAsync(intakeRecordId);

        // Nothing expired — a contract-less tenant surfaces exactly as before.
        var pactResult = PactSource(result);
        Assert.Equal("Found", pactResult.Status);
        Assert.Empty(Assert.Single(pactResult.Customers).Units);
    }

    [Fact]
    public async Task SearchExternalSourcesByPhoneAsync_AndIntakeLookup_BothRetainHistoricalContracts()
    {
        // The same seeded PACT customer, through both legs: the Customer
        // Workspace search keeps the expired contract (labelled) so the
        // agent can reach historical tickets, payments and fines; the
        // intake lookup retains the same evidence for New Ticket's selection view.
        var f = CreateService(new FakeTimeProvider(DubaiBoundaryUtc));
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Pact.Seed(Phone, new PactCustomerMatchDto(
            "PACT-CUST-1", "Fatima Noor", Phone, Email: null, CustomerType: null,
            Contracts: [Contract("U-EXPIRED", DubaiToday.AddDays(-1)), Contract("U-FUTURE", DubaiToday.AddDays(1))]));

        var workspace = await f.Service.SearchExternalSourcesByPhoneAsync(Phone);
        var wizard = await f.Service.SearchAsync(intakeRecordId);

        var workspaceCustomer = Assert.Single(Assert.Single(workspace, s => s.Source == "Pact").Customers);
        Assert.Equal(2, workspaceCustomer.Units.Count);
        Assert.True(workspaceCustomer.Units.Single(u => u.ExternalUnitId == "U-EXPIRED").IsContractExpired);
        Assert.False(workspaceCustomer.Units.Single(u => u.ExternalUnitId == "U-FUTURE").IsContractExpired);

        var wizardCustomer = Assert.Single(PactSource(wizard).Customers);
        Assert.Equal(workspaceCustomer.Units, wizardCustomer.Units);
    }

    [Fact]
    public async Task SearchExternalSourcesByPhoneAsync_AllContractsExpired_CustomerRemainsDiscoverableForTheWorkspace()
    {
        var f = CreateService(new FakeTimeProvider(DubaiBoundaryUtc));
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Pact.Seed(Phone, new PactCustomerMatchDto(
            "PACT-CUST-1", "Former Tenant", Phone, Email: null, CustomerType: null,
            Contracts: [Contract("U-1", DubaiToday.AddDays(-1))]));

        var workspace = await f.Service.SearchExternalSourcesByPhoneAsync(Phone);
        var wizard = await f.Service.SearchAsync(intakeRecordId);

        var workspacePact = Assert.Single(workspace, s => s.Source == "Pact");
        Assert.Equal("Found", workspacePact.Status);
        Assert.True(Assert.Single(Assert.Single(workspacePact.Customers).Units).IsContractExpired);
        Assert.Equal("Found", PactSource(wizard).Status);
        Assert.True(Assert.Single(Assert.Single(PactSource(wizard).Customers).Units).IsContractExpired);
    }

    [Fact]
    public async Task SearchAsync_PactUnavailable_ReturnsFailed()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Pact.ForcedOutcome = PactCustomerLookupOutcome.Unavailable;

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal("Failed", Assert.Single(result.Response!.Sources, s => s.Source == "Pact").Status);
    }

    [Theory]
    [InlineData(PactCustomerLookupOutcome.Unauthorized)]
    [InlineData(PactCustomerLookupOutcome.InvalidResponse)]
    public async Task SearchAsync_PactNonSuccessOutcome_ReturnsFailed_NeverThrowsAndNeverBlocks(PactCustomerLookupOutcome outcome)
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Pact.ForcedOutcome = outcome;

        var result = await f.Service.SearchAsync(intakeRecordId);

        // A misconfigured key or a garbage PACT body is reported exactly like
        // an outage: Failed — enrichment only, never a ticket-creation gate.
        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        Assert.Equal("Failed", Assert.Single(result.Response!.Sources, s => s.Source == "Pact").Status);
    }

    [Fact]
    public async Task SearchAsync_TasleehFindsMatch_ReturnsFound()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Tasleeh.Seed(Phone, new TasleehCustomerMatch("TSL-CUST-1", "Omar Khalid", Phone));

        var result = await f.Service.SearchAsync(intakeRecordId);

        var tasleehResult = Assert.Single(result.Response!.Sources, s => s.Source == "Tasleeh");
        Assert.Equal("Found", tasleehResult.Status);
        Assert.Equal("Omar Khalid", Assert.Single(tasleehResult.Customers).DisplayName);
    }

    [Fact]
    public async Task SearchAsync_TasleehUnavailable_ReturnsFailed()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords);
        f.Tasleeh.ThrowUnavailable = true;

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal("Failed", Assert.Single(result.Response!.Sources, s => s.Source == "Tasleeh").Status);
    }

    [Fact]
    public async Task SearchAsync_NoDepartment_PartialResults_CrmFoundPactFailedTasleehNotFound_ReturnsAllThreeTogether()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords, departmentId: null);
        var unit = new TigerCS.Application.Modules.CustomerVerification.CrmIntegration.CrmUnitResult("CRM-UNIT-1001", "1204", "Tiger Tower A", "Tower A", "Residential");
        var contact = new TigerCS.Application.Modules.CustomerVerification.CrmIntegration.CrmContactResult(
            "CRM-CONTACT-2001", "Ahmed Al-Farsi", Phone, ContactType.Owner, null);
        f.CrmUnitGateway.Seed(unit, contact);
        f.Crm.Seed(Phone, new CrmCustomerMatch(
            "CRM-CUST-9001", contact.DisplayName, Phone, null, "Buyer", [new CrmCustomerUnitMatch(unit.CrmUnitId, contact.CrmContactId)]));
        f.Pact.ForcedOutcome = PactCustomerLookupOutcome.Unavailable;
        // Tasleeh has no fixture for this phone number — NotFound.

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        Assert.Equal(3, result.Response!.Sources.Count);
        Assert.Equal("Found", Assert.Single(result.Response.Sources, s => s.Source == "Crm").Status);
        Assert.Equal("Failed", Assert.Single(result.Response.Sources, s => s.Source == "Pact").Status);
        Assert.Equal("NotFound", Assert.Single(result.Response.Sources, s => s.Source == "Tasleeh").Status);
    }

    [Fact]
    public async Task SearchAsync_NoDepartment_AllThreeSourcesFail_StillSucceedsWithThreeFailedEntries()
    {
        var f = CreateService();
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords, departmentId: null);
        f.Crm.ThrowUnavailable = true;
        f.Pact.ForcedOutcome = PactCustomerLookupOutcome.Unavailable;
        f.Tasleeh.ThrowUnavailable = true;

        var result = await f.Service.SearchAsync(intakeRecordId);

        // A source that cannot be reached is never a Ticket-creation gate —
        // the lookup itself still returns Success with three Failed entries,
        // rather than failing the whole request.
        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        Assert.Equal(3, result.Response!.Sources.Count);
        Assert.All(result.Response.Sources, s => Assert.Equal("Failed", s.Status));
    }

    [Fact]
    public async Task SearchAsync_DepartmentMappedToCrmOnly_SearchesOnlyCrm()
    {
        var f = CreateService();
        f.DepartmentSources.Seed(DepartmentId, CustomerLookupSource.Crm);
        f.Pact.Seed(Phone, new PactCustomerMatchDto("PACT-CUST-1", "Fatima Noor", Phone, Email: null, CustomerType: null, Contracts: []));
        f.Tasleeh.Seed(Phone, new TasleehCustomerMatch("TSL-CUST-1", "Omar Khalid", Phone));
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords, departmentId: DepartmentId);

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        var source = Assert.Single(result.Response!.Sources);
        Assert.Equal("Crm", source.Source);
        Assert.Equal(0, f.Pact.SearchCallCount);
        Assert.Equal(0, f.Tasleeh.SearchCallCount);
    }

    [Fact]
    public async Task SearchAsync_DepartmentMappedToPactOnly_SearchesOnlyPact()
    {
        var f = CreateService();
        f.DepartmentSources.Seed(DepartmentId, CustomerLookupSource.Pact);
        f.Pact.Seed(Phone, new PactCustomerMatchDto("PACT-CUST-1", "Fatima Noor", Phone, Email: null, CustomerType: null, Contracts: []));
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords, departmentId: DepartmentId);

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        var source = Assert.Single(result.Response!.Sources);
        Assert.Equal("Pact", source.Source);
        Assert.Equal("Found", source.Status);
        Assert.Equal(0, f.Crm.SearchCallCount);
        Assert.Equal(0, f.Tasleeh.SearchCallCount);
    }

    [Fact]
    public async Task SearchAsync_DepartmentMappedToTasleehOnly_SearchesOnlyTasleeh()
    {
        var f = CreateService();
        f.DepartmentSources.Seed(DepartmentId, CustomerLookupSource.Tasleeh);
        f.Tasleeh.Seed(Phone, new TasleehCustomerMatch("TSL-CUST-1", "Omar Khalid", Phone));
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords, departmentId: DepartmentId);

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        var source = Assert.Single(result.Response!.Sources);
        Assert.Equal("Tasleeh", source.Source);
        Assert.Equal("Found", source.Status);
        Assert.Equal(0, f.Crm.SearchCallCount);
        Assert.Equal(0, f.Pact.SearchCallCount);
    }

    [Fact]
    public async Task SearchAsync_DepartmentMappedToCrmAndTasleeh_SearchesOnlyThoseTwo_NeverPact()
    {
        var f = CreateService();
        f.DepartmentSources.Seed(DepartmentId, CustomerLookupSource.Crm, CustomerLookupSource.Tasleeh);
        f.Tasleeh.Seed(Phone, new TasleehCustomerMatch("TSL-CUST-1", "Omar Khalid", Phone));
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords, departmentId: DepartmentId);

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        Assert.Equal(2, result.Response!.Sources.Count);
        Assert.Contains(result.Response.Sources, s => s.Source == "Crm");
        Assert.Contains(result.Response.Sources, s => s.Source == "Tasleeh");
        Assert.DoesNotContain(result.Response.Sources, s => s.Source == "Pact");
        Assert.Equal(0, f.Pact.SearchCallCount);
    }

    [Fact]
    public async Task SearchAsync_DepartmentConfiguredSourceReturnsNotFound_DoesNotFallBackToOtherSources()
    {
        var f = CreateService();
        f.DepartmentSources.Seed(DepartmentId, CustomerLookupSource.Crm);
        // CRM has no fixture for this phone number (NotFound). PACT/Tasleeh
        // have matches seeded, but the Department is scoped to CRM only —
        // never an automatic fallback to the other sources.
        f.Pact.Seed(Phone, new PactCustomerMatchDto("PACT-CUST-1", "Fatima Noor", Phone, Email: null, CustomerType: null, Contracts: []));
        f.Tasleeh.Seed(Phone, new TasleehCustomerMatch("TSL-CUST-1", "Omar Khalid", Phone));
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords, departmentId: DepartmentId);

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        var source = Assert.Single(result.Response!.Sources);
        Assert.Equal("Crm", source.Source);
        Assert.Equal("NotFound", source.Status);
        Assert.Equal(0, f.Pact.SearchCallCount);
        Assert.Equal(0, f.Tasleeh.SearchCallCount);
    }

    [Fact]
    public async Task SearchAsync_DepartmentWithNoConfiguredSources_SearchesNothing()
    {
        var f = CreateService();
        // Deliberately not seeded — a Department with zero configured
        // sources searches nothing rather than silently falling back to all
        // three (never an automatic fallback unless explicitly configured).
        var intakeRecordId = await SeedIntakeAsync(f.IntakeRecords, departmentId: DepartmentId);

        var result = await f.Service.SearchAsync(intakeRecordId);

        Assert.Equal(CustomerLookupOutcome.Success, result.Outcome);
        Assert.Empty(result.Response!.Sources);
        Assert.Equal(0, f.Crm.SearchCallCount);
        Assert.Equal(0, f.Pact.SearchCallCount);
        Assert.Equal(0, f.Tasleeh.SearchCallCount);
    }
}
